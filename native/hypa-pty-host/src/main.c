/*
 * hypa-pty-host — native PTY helper for hypa-runtime.
 * Speaks length-prefixed frames on stdio. See docs/plans/AgentRuntime/pty-host-ipc.md.
 *
 * P0: one helper process = one child session. No network. Same unprivileged user.
 *: PauseOutput / Adopt / Adopted / CloseOldOwner / ResumeHandoff /
 *       ReleaseAdopted with SCM_RIGHTS via AF_UNIX.
 */

#include "env.h"
#include "fdpass.h"
#include "ipc.h"
#include "session.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <util.h>
#else
#include <pty.h>
#endif

#if defined(__linux__) || defined(__APPLE__)
/* posix_openpt / grantpt / unlockpt for --selftest-fdpass */
#endif

/*
 * Safety-only abandon after a long pause without CloseOldOwner / ResumeHandoff.
 * Managed export budget is shorter (default 5s). After HandleMeta the managed
 * side must NOT ResumeHandoff while a peer may still own the FD (fail-closed
 * single authority). Resume only after peer Abort proves ReleaseAdopted.
 *
 * On safety expiry we MUST NOT unpause and pump: a peer may have Adopted and
 * dual-pump would steal terminal bytes. Instead close master only, mark
 * handed_off, and never process-group kill the child.
 */
#define HYPTY_HANDOFF_PAUSE_MS 30000
#define HYPTY_NONCE_LEN 16

static volatile sig_atomic_t g_got_sigchld = 0;

/*
 * Safety timeout for PauseOutput without CloseOldOwner / ResumeHandoff.
 * Production default: 30s. Tests may set HYPTY_HANDOFF_PAUSE_MS (ms, 50–120000).
 */
static int handoff_pause_ms(void)
{
    const char *e = getenv("HYPTY_HANDOFF_PAUSE_MS");
    char *end = NULL;
    long v;

    if (!e || !*e)
        return HYPTY_HANDOFF_PAUSE_MS;
    errno = 0;
    v = strtol(e, &end, 10);
    if (errno != 0 || end == e || v < 50 || v > 120000)
        return HYPTY_HANDOFF_PAUSE_MS;
    return (int)v;
}

/*
 * Abnormal teardown (stdin HUP, frame read fail, main done):
 *  - adopted: close master only (never process-group kill)
 *  - paused handoff (leave_alive): close master only — peer may hold a
 * duplicate master; process-group kill would violate criteria 6/7
 *  - otherwise: terminate the session as the intentional owner
 */
static void session_teardown_abnormal(struct hypty_session *session, int leave_alive)
{
    if (!session)
        return;
    if (leave_alive || session->is_adopted)
        hypty_session_close_master(session);
    else
        hypty_session_terminate(session, 2000);
}

static void on_sigchld(int sig)
{
    (void)sig;
    g_got_sigchld = 1;
}

static uint16_t read_u16_le(const uint8_t *p)
{
    return (uint16_t)(p[0] | (p[1] << 8));
}

static int32_t read_i32_le(const uint8_t *p)
{
    return (int32_t)((uint32_t)p[0]
        | ((uint32_t)p[1] << 8)
        | ((uint32_t)p[2] << 16)
        | ((uint32_t)p[3] << 24));
}

static uint32_t read_u32_le(const uint8_t *p)
{
    return (uint32_t)p[0]
        | ((uint32_t)p[1] << 8)
        | ((uint32_t)p[2] << 16)
        | ((uint32_t)p[3] << 24);
}

static void write_i32_le(uint8_t *p, int32_t v)
{
    uint32_t u = (uint32_t)v;
    p[0] = (uint8_t)(u & 0xff);
    p[1] = (uint8_t)((u >> 8) & 0xff);
    p[2] = (uint8_t)((u >> 16) & 0xff);
    p[3] = (uint8_t)((u >> 24) & 0xff);
}

static int64_t now_ms(void)
{
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0)
        return 0;
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

/*
 * Pump master → Output.
 * Returns:
 *   1  — data written
 *   0  — no data / soft retry (EAGAIN, EINTR, adopted EOF while child alive)
 *   2  — child proven dead (exit marked); caller should emit Exit
 *  -1  — fatal (stdout write failure / unexpected errno)
 *
 * After H2 Adopt the helper is not the parent: waitpid cannot observe exit.
 * Master EOF/EIO alone is NOT death proof (child may close slaves and live).
 * Adopted Exit requires kill(pid,0) ESRCH via hypty_session_try_reap.
 */
static int pump_master_output(struct hypty_session *session)
{
    uint8_t buf[HYPTY_MAX_CHUNK];
    ssize_t n;

    if (!session || session->master_fd < 0)
        return 0;

    n = read(session->master_fd, buf, sizeof(buf));
    if (n > 0) {
        /*
         * Blocking Output write (full fidelity). PtyHostProcess never blocks its
         * frame pump on the consumer channel (TryWrite only), so helper stdout
         * keeps draining even when StandardOutput readers cancel — write_full
         * cannot stall forever. stdin is preferred over master in the poll loop
         * so Close is handled as soon as we return from a write.
         */
        if (hypty_write_output(STDOUT_FILENO, buf, (uint32_t)n) != 0)
            return -1;
        return 1;
    }
    if (n == 0 || (n < 0 && errno == EIO)) {
        /*
         * EOF: all slave ends closed. EIO (Linux): often session leader gone,
         * but not always — a live child can close its PTY descriptors.
         *
         * Adopted: require kill(pid,0) death proof before Exit. If still alive,
         * mark master_eof so we stop re-polling the drained master (no busy-spin)
         * and keep probing liveness in the main loop.
         *
         * Spawned: only waitpid sets child_exited. Soft EIO/EOF without a reap
         * is non-fatal so a transient master condition cannot emit Exit while
         * the OS pid lives.
         */
        if (session->is_adopted) {
            if (hypty_session_try_reap(session) == 1)
                return 2;
            session->master_eof = 1;
            return 0;
        }
        if (!session->child_exited)
            (void)hypty_session_try_reap(session);
        if (session->child_exited)
            return 2;
        /* Soft retry — waitpid will observe real death. */
        return 0;
    }
    if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)
        return 0;
    return -1;
}

/* Drain remaining master bytes then emit Exit once. */
static int emit_exit_if_needed(struct hypty_session *session, int *exit_sent)
{
    if (!session || !session->child_exited || *exit_sent)
        return 0;
    for (;;) {
        int r = pump_master_output(session);
        /* Only keep draining while real data was written (r==1). */
        if (r != 1)
            break;
    }
    hypty_session_close_master(session);
    if (hypty_write_exit(STDOUT_FILENO, session->exit_code, (int32_t)session->child_pid) != 0)
        return -1;
    *exit_sent = 1;
    return 0;
}

/* Copy path from payload into NUL-terminated stack buffer. */
static int copy_path(const uint8_t *p, uint32_t len, char *out, size_t out_sz)
{
    if (!hypty_path_is_absolute_uds((const char *)p, len))
        return -1;
    if (len + 1 > out_sz)
        return -1;
    memcpy(out, p, len);
    out[len] = '\0';
    return 0;
}

/* Forward decl: optional CLI self-tests. */
static int run_fdpass_selftest(void);
static int run_env_overflow_selftest(void);
static int run_input_eagain_selftest(void);
static int run_sigpipe_selftest(void);

/* Pump Output while a non-blocking Input write waits for POLLOUT. */
static int pump_master_for_input(int master_fd, void *ctx)
{
    int r;

    (void)master_fd;
    r = pump_master_output((struct hypty_session *)ctx);
    return r < 0 ? -1 : 0;
}

struct pending_input {
    uint8_t *owned;
    const uint8_t *p;
    uint32_t left;
    struct pending_input *next;
};

static void pending_input_free_from(struct pending_input *n)
{
    while (n) {
        struct pending_input *nx = n->next;
        if (n->owned) {
            free(n->owned);
            free(n);
        }
        n = nx;
    }
}

/*
 * Write one Input payload. On EAGAIN, wait POLLOUT with stdin so Close/Signal
 * win. Extra Input frames that arrive mid-write are queued (never dropped).
 *
 * Returns:
 *   0 — written (or soft I/O error already reported)
 *   1 — Close handled; caller goto done
 *   2 — stdin dead; caller teardown + goto done
 */
static int handle_input_frames(struct hypty_session *session, int paused,
    const uint8_t *payload, uint32_t payload_len, int *exit_sent)
{
    struct pending_input first;
    struct pending_input *head;
    struct pending_input *tail;

    if (!session || session->master_fd < 0 || !payload || payload_len == 0)
        return 0;

    first.owned = NULL;
    first.p = payload;
    first.left = payload_len;
    first.next = NULL;
    head = &first;
    tail = &first;

    while (head) {
        uint32_t wrote = 0;
        int wr;
        struct hypty_frame ctl;
        uint8_t *ctl_buf = NULL;

        if (head->left == 0) {
            struct pending_input *done = head;
            head = head->next;
            if (done->owned) {
                free(done->owned);
                free(done);
            }
            continue;
        }

        wr = hypty_write_master_all(session->master_fd, head->p, head->left,
            STDIN_FILENO, &wrote, pump_master_for_input, session);
        if (wr < 0) {
            hypty_write_error(STDOUT_FILENO, HYPTY_ERR_IO, "master input write failed");
            pending_input_free_from(head);
            return 0;
        }
        if (wr == 0) {
            head->left = 0;
            continue;
        }

        head->p += wrote;
        head->left -= wrote;

        if (hypty_frame_read(STDIN_FILENO, &ctl, &ctl_buf) != 0) {
            free(ctl_buf);
            pending_input_free_from(head);
            return 2;
        }

        if (ctl.type == HYPTY_FRAME_CLOSE) {
            free(ctl_buf);
            pending_input_free_from(head);
            hypty_session_terminate(session, 2000);
            if (exit_sent && !*exit_sent) {
                hypty_write_exit(STDOUT_FILENO, session->exit_code,
                    (int32_t)session->child_pid);
                *exit_sent = 1;
            }
            return 1;
        }

        if (ctl.type == HYPTY_FRAME_SIGNAL && ctl.payload_len >= 4) {
            if (!paused)
                hypty_session_signal_group(session, (int)read_i32_le(ctl.payload));
            else
                hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF,
                    "signal during handoff pause");
            free(ctl_buf);
            continue;
        }

        if (ctl.type == HYPTY_FRAME_RESIZE && ctl.payload_len >= 4) {
            if (!paused) {
                uint16_t c = read_u16_le(ctl.payload);
                uint16_t rws = read_u16_le(ctl.payload + 2);
                hypty_session_resize(session, c, rws);
            } else {
                hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF,
                    "resize during handoff pause");
            }
            free(ctl_buf);
            continue;
        }

        if (ctl.type == HYPTY_FRAME_INPUT && ctl.payload_len > 0) {
            struct pending_input *extra;

            extra = (struct pending_input *)malloc(sizeof(*extra));
            if (!extra) {
                free(ctl_buf);
                hypty_write_error(STDOUT_FILENO, HYPTY_ERR_INTERNAL,
                    "input queue alloc failed");
                pending_input_free_from(head);
                return 0;
            }
            extra->owned = ctl_buf;
            extra->p = ctl.payload;
            extra->left = ctl.payload_len;
            extra->next = NULL;
            tail->next = extra;
            tail = extra;
            continue;
        }

        hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL,
            "unexpected frame during input write");
        free(ctl_buf);
    }

    return 0;
}

static int set_fd_nonblock(int fd)
{
    int flags;
    if (fd < 0)
        return -1;
    flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0)
        return -1;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

int main(int argc, char **argv)
{
    struct hypty_session session;
    int have_session = 0;
    int exit_sent = 0;
    int paused = 0;
    int handed_off = 0;
    int released_adopted = 0;
    uint8_t pause_nonce[HYPTY_NONCE_LEN];
    int32_t pause_generation = 0;
    int have_pause_identity = 0;
    int64_t pause_started_ms = 0;
    int pause_budget_ms;
    struct sigaction sa;

    if (argc >= 2 && argv[1] && strcmp(argv[1], "--selftest-fdpass") == 0)
        return run_fdpass_selftest();
    if (argc >= 2 && argv[1] && strcmp(argv[1], "--selftest-env-overflow") == 0)
        return run_env_overflow_selftest();
    if (argc >= 2 && argv[1] && strcmp(argv[1], "--selftest-input-eagain") == 0)
        return run_input_eagain_selftest();
    if (argc >= 2 && argv[1] && strcmp(argv[1], "--selftest-sigpipe") == 0)
        return run_sigpipe_selftest();

    memset(&session, 0, sizeof(session));
    session.master_fd = -1;
    session.child_pid = -1;
    memset(pause_nonce, 0, sizeof(pause_nonce));
    pause_budget_ms = handoff_pause_ms();

    /* Ignore SIGPIPE so writes to a dead parent fail cleanly. */
    signal(SIGPIPE, SIG_IGN);

    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = on_sigchld;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_RESTART | SA_NOCLDSTOP;
    sigaction(SIGCHLD, &sa, NULL);

    /* Parent death: when stdin closes we terminate (unless handed off / paused). */

    for (;;) {
        struct pollfd pfds[2];
        int nfds = 0;
        int stdin_idx = -1;
        int master_idx = -1;
        int pr;

        /*
         * Safety-only abandon: if managed never ResumeHandoff/CloseOldOwner,
         * drop master without unpause-pump (avoids dual-pump after Adopt) and
         * without process-group kill. Late CloseOldOwner fails closed (no
         * pause identity). Prefer managed ResumeHandoff on proven peer release.
         */
        if (paused && have_pause_identity && have_session) {
            int64_t elapsed = now_ms() - pause_started_ms;
            if (elapsed >= (int64_t)pause_budget_ms) {
                hypty_session_close_master(&session);
                have_session = 0;
                paused = 0;
                have_pause_identity = 0;
                handed_off = 1;
                session.child_pid = -1;
                goto done;
            }
        }

        pfds[nfds].fd = STDIN_FILENO;
        pfds[nfds].events = POLLIN;
        stdin_idx = nfds++;

        /* Skip drained master after adopted EOF while child still alive. */
        if (have_session && session.master_fd >= 0 && !session.child_exited
            && !paused && !session.master_eof) {
            pfds[nfds].fd = session.master_fd;
            pfds[nfds].events = POLLIN;
            master_idx = nfds++;
        }

        pr = poll(pfds, (nfds_t)nfds, 100);
        if (pr < 0) {
            if (errno == EINTR)
                goto reap;
            break;
        }

        /*
         * Prefer stdin (Close/Signal/Input) over master Output. A chatty child
         * with a stalled parent must not starve terminate: master was previously
         * handled first and write_full could block before Close was read.
         */
        if (stdin_idx >= 0 && (pfds[stdin_idx].revents & (POLLHUP | POLLERR))) {
            /*
             * Parent closed stdin. During pause / after adopt, abandon master
             * only — never process-group kill the interactive child.
             */
            if (have_session && !handed_off && !released_adopted) {
                session_teardown_abnormal(&session, paused);
                if (paused) {
                    /* Leave-alive: mark handoff-complete so done: does not kill. */
                    handed_off = 1;
                    have_session = 0;
                    paused = 0;
                    have_pause_identity = 0;
                    session.child_pid = -1;
                }
            }
            break;
        }

        if (stdin_idx >= 0 && (pfds[stdin_idx].revents & POLLIN)) {
            struct hypty_frame frame;
            uint8_t *buf = NULL;

            if (hypty_frame_read(STDIN_FILENO, &frame, &buf) != 0) {
                free(buf);
                if (have_session && !handed_off && !released_adopted) {
                    session_teardown_abnormal(&session, paused);
                    if (paused) {
                        handed_off = 1;
                        have_session = 0;
                        paused = 0;
                        have_pause_identity = 0;
                        session.child_pid = -1;
                    }
                }
                break;
            }

            switch (frame.type) {
            case HYPTY_FRAME_HELLO: {
                uint16_t minor = 0;
                if (hypty_parse_hello(frame.payload, frame.payload_len, &minor) != 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "bad hello");
                    free(buf);
                    goto done;
                }
                if (hypty_write_hello(STDOUT_FILENO) != 0) {
                    free(buf);
                    goto done;
                }
                break;
            }
            case HYPTY_FRAME_SPAWN:
                if (have_session) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "session already active");
                    break;
                }
                if (hypty_session_spawn(&session, frame.payload, frame.payload_len) != 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_SPAWN, "spawn failed");
                    break;
                }
                have_session = 1;
                exit_sent = 0;
                paused = 0;
                handed_off = 0;
                /* ack child pid so managed adapter can set IPtyProcess.Pid.*/
                if (hypty_write_spawned(STDOUT_FILENO, (int32_t)session.child_pid) != 0) {
                    free(buf);
                    goto done;
                }
                break;
            case HYPTY_FRAME_INPUT:
                if (paused) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "input during handoff pause");
                    break;
                }
                if (have_session && session.master_fd >= 0 && frame.payload_len > 0) {
                    int in_rc = handle_input_frames(&session, paused, frame.payload,
                        frame.payload_len, &exit_sent);
                    if (in_rc == 1) {
                        free(buf);
                        goto done;
                    }
                    if (in_rc == 2) {
                        free(buf);
                        if (have_session && !handed_off && !released_adopted)
                            session_teardown_abnormal(&session, paused);
                        goto done;
                    }
                }
                break;
            case HYPTY_FRAME_RESIZE:
                if (paused) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "resize during handoff pause");
                    break;
                }
                if (have_session && frame.payload_len >= 4) {
                    uint16_t c = read_u16_le(frame.payload);
                    uint16_t r = read_u16_le(frame.payload + 2);
                    hypty_session_resize(&session, c, r);
                }
                break;
            case HYPTY_FRAME_SIGNAL:
                if (paused) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "signal during handoff pause");
                    break;
                }
                if (have_session && frame.payload_len >= 4) {
                    int32_t sig = read_i32_le(frame.payload);
                    hypty_session_signal_group(&session, (int)sig);
                }
                break;
            case HYPTY_FRAME_CLOSE:
                if (have_session && !handed_off) {
                    hypty_session_terminate(&session, 2000);
                    if (!exit_sent) {
                        hypty_write_exit(STDOUT_FILENO, session.exit_code, (int32_t)session.child_pid);
                        exit_sent = 1;
                    }
                }
                free(buf);
                goto done;
            case HYPTY_FRAME_PAUSE_OUTPUT: {
                /* nonce[16] + generation i32 + path_len u32 + path */
                char path[sizeof(((struct sockaddr_un *)0)->sun_path)];
                uint32_t path_len;
                const uint8_t *p;
                int32_t gen;

                if (!have_session || session.master_fd < 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "pause without session");
                    break;
                }
                if (frame.payload_len < HYPTY_NONCE_LEN + 4 + 4) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "pause payload short");
                    break;
                }
                p = frame.payload;
                memcpy(pause_nonce, p, HYPTY_NONCE_LEN);
                p += HYPTY_NONCE_LEN;
                gen = read_i32_le(p);
                p += 4;
                path_len = read_u32_le(p);
                p += 4;
                if ((uint32_t)(frame.payload_len - (HYPTY_NONCE_LEN + 8)) < path_len) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "pause path truncated");
                    break;
                }
                if (copy_path(p, path_len, path, sizeof(path)) != 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "pause path not absolute");
                    break;
                }

                if (hypty_send_fd(path, session.master_fd) != 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "pause send_fd failed");
                    break;
                }

                pause_generation = gen;
                have_pause_identity = 1;
                paused = 1;
                pause_started_ms = now_ms();
                break;
            }
            case HYPTY_FRAME_ADOPT: {
                /* nonce[16] + generation i32 + child_pid i32 + path_len u32 + path */
                char path[sizeof(((struct sockaddr_un *)0)->sun_path)];
                uint32_t path_len;
                const uint8_t *p;
                int32_t gen;
                int32_t child_pid;
                int master = -1;
                uint8_t adopted[HYPTY_NONCE_LEN + 8];

                if (have_session) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "adopt with session");
                    break;
                }
                if (frame.payload_len < HYPTY_NONCE_LEN + 4 + 4 + 4) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "adopt payload short");
                    break;
                }
                p = frame.payload;
                memcpy(adopted, p, HYPTY_NONCE_LEN);
                p += HYPTY_NONCE_LEN;
                gen = read_i32_le(p);
                p += 4;
                child_pid = read_i32_le(p);
                p += 4;
                path_len = read_u32_le(p);
                p += 4;
                if ((uint32_t)(frame.payload_len - (HYPTY_NONCE_LEN + 12)) < path_len) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "adopt path truncated");
                    break;
                }
                if (copy_path(p, path_len, path, sizeof(path)) != 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "adopt path not absolute");
                    break;
                }
                if (child_pid <= 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "adopt bad pid");
                    break;
                }

                if (hypty_recv_fd(path, &master) != 0 || master < 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "adopt recv_fd failed");
                    break;
                }
                if (set_fd_nonblock(master) != 0) {
                    close(master);
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "adopt nonblock failed");
                    break;
                }

                memset(&session, 0, sizeof(session));
                session.master_fd = master;
                session.child_pid = (pid_t)child_pid;
                session.child_exited = 0;
                session.exit_code = 0;
                session.master_eof = 0;
                /* Not the parent — never waitpid; use kill(0) + master EIO/EOF. */
                session.is_adopted = 1;
                have_session = 1;
                exit_sent = 0;
                paused = 0;
                handed_off = 0;

                write_i32_le(adopted + HYPTY_NONCE_LEN, gen);
                write_i32_le(adopted + HYPTY_NONCE_LEN + 4, child_pid);
                if (hypty_frame_write(STDOUT_FILENO, HYPTY_FRAME_ADOPTED, adopted,
                        (uint32_t)sizeof(adopted)) != 0) {
                    free(buf);
                    goto done;
                }
                break;
            }
            case HYPTY_FRAME_ADOPTED:
                /* Helper never receives Adopted. */
                hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "unexpected adopted");
                break;
            case HYPTY_FRAME_CLOSE_OLD_OWNER: {
                /* nonce[16] + generation i32 — close master only; never kill child. */
                const uint8_t *p;
                int32_t gen;

                if (frame.payload_len < HYPTY_NONCE_LEN + 4) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "close_old payload short");
                    break;
                }
                if (!have_session || !have_pause_identity) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "close_old without pause");
                    break;
                }
                p = frame.payload;
                if (memcmp(p, pause_nonce, HYPTY_NONCE_LEN) != 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "close_old nonce mismatch");
                    break;
                }
                gen = read_i32_le(p + HYPTY_NONCE_LEN);
                if (gen != pause_generation) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "close_old generation mismatch");
                    /* Stay paused with session; do not exit the helper. */
                    break;
                }

                /* Release master only. Child continues under new owner. */
                hypty_session_close_master(&session);
                have_session = 0;
                paused = 0;
                have_pause_identity = 0;
                handed_off = 1;
                session.child_pid = -1;
                free(buf);
                goto done;
            }
            case HYPTY_FRAME_RESUME_HANDOFF: {
                /* Fail-closed export after PauseOutput: unpause, keep session. */
                if (!have_session) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "resume without session");
                    break;
                }
                paused = 0;
                have_pause_identity = 0;
                break;
            }
            case HYPTY_FRAME_RELEASE_ADOPTED: {
                /*
                 * New owner abort after Adopt: close master only, never signal
                 * the process group. Old owner remains authoritative with live child.
                 */
                if (!have_session || session.master_fd < 0) {
                    hypty_write_error(STDOUT_FILENO, HYPTY_ERR_HANDOFF, "release without session");
                    break;
                }
                hypty_session_close_master(&session);
                have_session = 0;
                paused = 0;
                have_pause_identity = 0;
                handed_off = 0;
                released_adopted = 1;
                session.child_pid = -1;
                free(buf);
                goto done;
            }
            default:
                hypty_write_error(STDOUT_FILENO, HYPTY_ERR_PROTOCOL, "unknown frame type");
                break;
            }
            free(buf);
        }

        if (master_idx >= 0 && (pfds[master_idx].revents & (POLLIN | POLLHUP | POLLERR))) {
            int r = pump_master_output(&session);
            if (r < 0)
                break;
            /* r==2: master EOF/EIO — session marked exited; emit Exit below. */
        }

reap:
        if (have_session && !session.child_exited && !handed_off) {
            if (g_got_sigchld || session.is_adopted || 1) {
                g_got_sigchld = 0;
                (void)hypty_session_try_reap(&session);
            }
        }
        if (have_session && session.child_exited && !exit_sent && !handed_off) {
            /*
             * While paused for handoff, never close master / emit Exit from a
             * soft reap race — that would SIGHUP the child mid-export. Exit is
             * deferred until unpause (Resume/CloseOld) or non-paused operation.
             */
            if (paused)
                continue;
            if (emit_exit_if_needed(&session, &exit_sent) < 0)
                break;
        }
    }

done:
    if (have_session && !handed_off && !released_adopted) {
        if (session.is_adopted || paused) {
            /*
             * Adopted or mid-handoff pause: close master only — never murder
             * a live interactive child.
             */
            hypty_session_close_master(&session);
        } else {
            if (!session.child_exited)
                hypty_session_terminate(&session, 1000);
            if (!exit_sent && session.child_pid > 0)
                hypty_write_exit(STDOUT_FILENO, session.exit_code, (int32_t)session.child_pid);
            hypty_session_close_master(&session);
        }
    } else if (have_session && (handed_off || released_adopted)) {
        /* CloseOldOwner / ReleaseAdopted / safety abandon — master only. */
        hypty_session_close_master(&session);
    }
    return 0;
}

/*
 * --selftest-env-overflow: uint32 wrap / huge klen and cwd_len must fail closed
 * with no crash. Invoked from managed tests.
 */
static int run_env_overflow_selftest(void)
{
    uint8_t env_buf[32];
    uint8_t spawn_buf[16];
    char **envp = NULL;
    struct hypty_session session;

    memset(env_buf, 0, sizeof(env_buf));
    /* envc = 1, klen = UINT32_MAX-1 — off + klen + 4 wraps. */
    env_buf[0] = 1;
    env_buf[4] = 0xfe;
    env_buf[5] = 0xff;
    env_buf[6] = 0xff;
    env_buf[7] = 0xff;
    if (hypty_env_from_spawn(env_buf, (uint32_t)sizeof(env_buf), 0, &envp, NULL) == 0) {
        hypty_env_free(envp);
        return 1;
    }

    memset(env_buf, 0, sizeof(env_buf));
    /* envc = 1, klen = 0, vlen = UINT32_MAX-1. */
    env_buf[0] = 1;
    env_buf[8] = 0xfe;
    env_buf[9] = 0xff;
    env_buf[10] = 0xff;
    env_buf[11] = 0xff;
    envp = NULL;
    if (hypty_env_from_spawn(env_buf, (uint32_t)sizeof(env_buf), 0, &envp, NULL) == 0) {
        hypty_env_free(envp);
        return 2;
    }

    memset(spawn_buf, 0, sizeof(spawn_buf));
    spawn_buf[0] = 80; /* cols */
    spawn_buf[2] = 24; /* rows */
    /* cwd_len = UINT32_MAX-1 at offset 4 — off + cwd_len wraps. */
    spawn_buf[4] = 0xfe;
    spawn_buf[5] = 0xff;
    spawn_buf[6] = 0xff;
    spawn_buf[7] = 0xff;
    memset(&session, 0, sizeof(session));
    session.master_fd = -1;
    session.child_pid = -1;
    if (hypty_session_spawn(&session, spawn_buf, (uint32_t)sizeof(spawn_buf)) == 0) {
        hypty_session_close_master(&session);
        return 3;
    }

    return 0;
}

/*
 * --selftest-input-eagain: O_NONBLOCK master must not drop Input on EAGAIN.
 * Fill the slave buffer, then drain after a short hold. Exit 0 on success.
 */
static int run_input_eagain_selftest(void)
{
    int master = -1;
    int slave = -1;
    pid_t child = -1;
    uint8_t *payload = NULL;
    const uint32_t n = 256u * 1024u;
    uint32_t written = 0;
    int status = 0;
    int rc = 1;

    if (openpty(&master, &slave, NULL, NULL, NULL) != 0)
        return 2;
    {
        struct termios tio;
        if (tcgetattr(slave, &tio) != 0) {
            rc = 13;
            goto out;
        }
        cfmakeraw(&tio);
        if (tcsetattr(slave, TCSANOW, &tio) != 0) {
            rc = 14;
            goto out;
        }
    }
    if (set_fd_nonblock(master) != 0) {
        rc = 3;
        goto out;
    }
    payload = (uint8_t *)malloc(n);
    if (!payload) {
        rc = 5;
        goto out;
    }
    memset(payload, 'A', n);

    child = fork();
    if (child < 0) {
        rc = 6;
        goto out;
    }
    if (child == 0) {
        uint8_t buf[4096];
        uint32_t got = 0;

        close(master);
        /* Hold the slave unread so the parent write hits EAGAIN. */
        usleep(200 * 1000);
        while (got < n) {
            ssize_t r = read(slave, buf, sizeof(buf));
            if (r < 0) {
                if (errno == EINTR)
                    continue;
                _exit(7);
            }
            if (r == 0)
                _exit(8);
            got += (uint32_t)r;
        }
        _exit(0);
    }

    close(slave);
    slave = -1;

    /* control_fd=-1: only POLLOUT. A closed pipe would HUP and abort the write. */
    if (hypty_write_master_all(master, payload, n, -1, &written, NULL, NULL) != 0) {
        rc = 9;
        goto out;
    }
    if (written != n) {
        rc = 10;
        goto out;
    }
    if (waitpid(child, &status, 0) != child) {
        rc = 11;
        goto out;
    }
    child = -1;
    if (!WIFEXITED(status) || WEXITSTATUS(status) != 0) {
        rc = 12;
        goto out;
    }
    rc = 0;

out:
    if (child > 0) {
        (void)kill(child, SIGKILL);
        (void)waitpid(child, NULL, 0);
    }
    free(payload);
    if (master >= 0)
        close(master);
    if (slave >= 0)
        close(slave);
    return rc;
}

static void write_u16_le(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)(v & 0xff);
    p[1] = (uint8_t)((v >> 8) & 0xff);
}

static void write_u32_le_bytes(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xff);
    p[1] = (uint8_t)((v >> 8) & 0xff);
    p[2] = (uint8_t)((v >> 16) & 0xff);
    p[3] = (uint8_t)((v >> 24) & 0xff);
}

/*
 * --selftest-sigpipe: parent keeps SIGPIPE=IGN (production helper).
 * Spawn via hypty_session_spawn so the pane execve path restores SIG_DFL.
 * Child `kill -s PIPE $$; printf SURVIVED` must die and must not print SURVIVED.
 * Timeout or SURVIVED is failure. yes|head is not proof (BSD yes exits on EPIPE).
 */
static int run_sigpipe_selftest(void)
{
    struct hypty_session session;
    uint8_t payload[256];
    uint32_t off = 0;
    const char *cwd = "/";
    const char *argv0 = "/bin/sh";
    const char *argv1 = "-c";
    /* Disposition probe: IGN survives and prints; DFL dies and does not print. */
    const char *argv2 = "kill -s PIPE $$; printf SURVIVED\\n";
    const char *ekey = "PATH";
    const char *eval = "/usr/bin:/bin:/usr/sbin:/sbin";
    uint32_t cwd_len;
    uint32_t a0;
    uint32_t a1;
    uint32_t a2;
    uint32_t klen;
    uint32_t vlen;
    int waited = 0;
    const int timeout_ms = 4000;
    char seen[512];
    size_t seen_len = 0;

    /* Match production parent: ignore SIGPIPE on the helper. */
    (void)signal(SIGPIPE, SIG_IGN);

    cwd_len = (uint32_t)strlen(cwd);
    a0 = (uint32_t)strlen(argv0);
    a1 = (uint32_t)strlen(argv1);
    a2 = (uint32_t)strlen(argv2);
    klen = (uint32_t)strlen(ekey);
    vlen = (uint32_t)strlen(eval);

    write_u16_le(payload + off, 80);
    off += 2;
    write_u16_le(payload + off, 24);
    off += 2;
    write_u32_le_bytes(payload + off, cwd_len);
    off += 4;
    memcpy(payload + off, cwd, cwd_len);
    off += cwd_len;
    write_u32_le_bytes(payload + off, 3);
    off += 4;
    write_u32_le_bytes(payload + off, a0);
    off += 4;
    memcpy(payload + off, argv0, a0);
    off += a0;
    write_u32_le_bytes(payload + off, a1);
    off += 4;
    memcpy(payload + off, argv1, a1);
    off += a1;
    write_u32_le_bytes(payload + off, a2);
    off += 4;
    memcpy(payload + off, argv2, a2);
    off += a2;
    write_u32_le_bytes(payload + off, 1);
    off += 4;
    write_u32_le_bytes(payload + off, klen);
    off += 4;
    memcpy(payload + off, ekey, klen);
    off += klen;
    write_u32_le_bytes(payload + off, vlen);
    off += 4;
    memcpy(payload + off, eval, vlen);
    off += vlen;

    if (off > sizeof(payload))
        return 2;

    if (hypty_session_spawn(&session, payload, off) != 0)
        return 3;

    memset(seen, 0, sizeof(seen));
    while (waited < timeout_ms) {
        uint8_t drain[256];
        ssize_t nread;

        if (session.master_fd >= 0) {
            nread = read(session.master_fd, drain, sizeof(drain));
            if (nread > 0 && seen_len < sizeof(seen) - 1) {
                size_t room = sizeof(seen) - 1 - seen_len;
                size_t take = (size_t)nread < room ? (size_t)nread : room;
                memcpy(seen + seen_len, drain, take);
                seen_len += take;
                seen[seen_len] = '\0';
            }
        }
        if (hypty_session_try_reap(&session) == 1)
            break;
        usleep(20 * 1000);
        waited += 20;
    }

    if (!session.child_exited) {
        hypty_session_terminate(&session, 200);
        hypty_session_close_master(&session);
        return 4;
    }

    hypty_session_close_master(&session);
    if (strstr(seen, "SURVIVED") != NULL)
        return 5;
    /* Clean exit means SIGPIPE was ignored. DFL dies with 128+SIGPIPE. */
    if (session.exit_code == 0)
        return 6;
    return 0;
}

/*
 * --selftest-fdpass: prove native hypty_send_fd / hypty_recv_fd reject non-PTY
 * and fd 0, and accept a real PTY master. Exit 0 on success, non-zero on
 * failure. Invoked from managed PtyHandoffPortTests (criterion 15).
 */
static int run_fdpass_selftest(void)
{
    int pipefd[2] = { -1, -1 };
    int master = -1;
    int rc = 1;
    char dir[] = "/tmp/hypa-fdpass-st-XXXXXX";
    char path[sizeof(((struct sockaddr_un *)0)->sun_path)];
    int listen_fd = -1;
    struct sockaddr_un addr;
    size_t plen;

    if (pipe(pipefd) != 0)
        return 2;

    /* Non-PTY must be rejected by send (no listener required). */
    if (hypty_send_fd("/tmp/does-not-matter.sock", pipefd[0]) == 0) {
        /* Should have failed is_pty_master before connect. */
        goto out;
    }

#if defined(__linux__) || defined(__APPLE__)
    master = posix_openpt(O_RDWR | O_NOCTTY);
    if (master < 0)
        goto out;
    if (grantpt(master) != 0 || unlockpt(master) != 0)
        goto out;

    if (!mkdtemp(dir))
        goto out;
    if (chmod(dir, 0700) != 0)
        goto out;

    if ((size_t)snprintf(path, sizeof(path), "%s/s.sock", dir) >= sizeof(path))
        goto out;

    listen_fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (listen_fd < 0)
        goto out;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    plen = strlen(path);
    if (plen >= sizeof(addr.sun_path))
        goto out;
    memcpy(addr.sun_path, path, plen + 1);
#if defined(__APPLE__)
    addr.sun_len = (unsigned char)(offsetof(struct sockaddr_un, sun_path) + plen + 1);
#endif
    (void)unlink(path);
    if (bind(listen_fd, (struct sockaddr *)&addr, sizeof(addr)) != 0)
        goto out;
    if (chmod(path, 0600) != 0)
        goto out;
    if (listen(listen_fd, 1) != 0)
        goto out;

    /* Accept path: helper connect+send in a forked child so we can accept. */
    {
        pid_t child = fork();
        if (child < 0)
            goto out;
        if (child == 0) {
            close(listen_fd);
            /* Parent will accept; send PTY master. */
            _exit(hypty_send_fd(path, master) == 0 ? 0 : 1);
        }

        {
            int conn = accept(listen_fd, NULL, NULL);
            int status = 0;
            int got = -1;
            if (conn < 0) {
                waitpid(child, NULL, 0);
                goto out;
            }
            /* Receive raw SCM_RIGHTS (not via hypty_recv_fd — that connects). */
            {
                struct msghdr msg;
                struct iovec iov;
                char dummy = 0;
                union {
                    struct cmsghdr align;
                    char buf[CMSG_SPACE(sizeof(int))];
                } cmsg_buf;
                struct cmsghdr *cmsg;

                memset(&msg, 0, sizeof(msg));
                memset(&cmsg_buf, 0, sizeof(cmsg_buf));
                iov.iov_base = &dummy;
                iov.iov_len = 1;
                msg.msg_iov = &iov;
                msg.msg_iovlen = 1;
                msg.msg_control = cmsg_buf.buf;
                msg.msg_controllen = sizeof(cmsg_buf.buf);
                if (recvmsg(conn, &msg, 0) >= 0) {
                    for (cmsg = CMSG_FIRSTHDR(&msg); cmsg; cmsg = CMSG_NXTHDR(&msg, cmsg)) {
                        if (cmsg->cmsg_level == SOL_SOCKET && cmsg->cmsg_type == SCM_RIGHTS
                            && cmsg->cmsg_len >= CMSG_LEN(sizeof(int))) {
                            memcpy(&got, CMSG_DATA(cmsg), sizeof(int));
                            break;
                        }
                    }
                }
            }
            close(conn);
            waitpid(child, &status, 0);
            if (!WIFEXITED(status) || WEXITSTATUS(status) != 0 || got < 0)
                goto out;
            close(got);
        }
    }

    /* Exact-mode negatives: socket 0000 / parent 0755 must fail path check. */
    {
        if (chmod(path, 0000) != 0)
            goto out;
        if (hypty_send_fd(path, master) == 0)
            goto out; /* must reject non-exact 0600 */
        if (chmod(path, 0600) != 0)
            goto out;

        if (chmod(dir, 0755) != 0)
            goto out;
        if (hypty_send_fd(path, master) == 0)
            goto out; /* must reject non-exact 0700 parent */
        if (chmod(dir, 0700) != 0)
            goto out;
    }

    /* Recv path: send a pipe via raw sendmsg; hypty_recv_fd must reject. */
    {
        char rpath[sizeof(path)];
        int rlisten = -1;
        pid_t child;

        if ((size_t)snprintf(rpath, sizeof(rpath), "%s/r.sock", dir) >= sizeof(rpath))
            goto out;
        rlisten = socket(AF_UNIX, SOCK_STREAM, 0);
        if (rlisten < 0)
            goto out;
        memset(&addr, 0, sizeof(addr));
        addr.sun_family = AF_UNIX;
        plen = strlen(rpath);
        memcpy(addr.sun_path, rpath, plen + 1);
#if defined(__APPLE__)
        addr.sun_len = (unsigned char)(offsetof(struct sockaddr_un, sun_path) + plen + 1);
#endif
        (void)unlink(rpath);
        if (bind(rlisten, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
            close(rlisten);
            goto out;
        }
        if (chmod(rpath, 0600) != 0) {
            close(rlisten);
            goto out;
        }
        if (listen(rlisten, 1) != 0) {
            close(rlisten);
            goto out;
        }

        child = fork();
        if (child < 0) {
            close(rlisten);
            goto out;
        }
        if (child == 0) {
            int conn = accept(rlisten, NULL, NULL);
            if (conn >= 0) {
                struct msghdr msg;
                struct iovec iov;
                char dummy = 0;
                union {
                    struct cmsghdr align;
                    char buf[CMSG_SPACE(sizeof(int))];
                } cmsg_buf;
                struct cmsghdr *cmsg;
                int send_fd = pipefd[0];

                memset(&msg, 0, sizeof(msg));
                memset(&cmsg_buf, 0, sizeof(cmsg_buf));
                iov.iov_base = &dummy;
                iov.iov_len = 1;
                msg.msg_iov = &iov;
                msg.msg_iovlen = 1;
                msg.msg_control = cmsg_buf.buf;
                msg.msg_controllen = sizeof(cmsg_buf.buf);
                cmsg = CMSG_FIRSTHDR(&msg);
                if (cmsg) {
                    cmsg->cmsg_level = SOL_SOCKET;
                    cmsg->cmsg_type = SCM_RIGHTS;
                    cmsg->cmsg_len = CMSG_LEN(sizeof(int));
                    memcpy(CMSG_DATA(cmsg), &send_fd, sizeof(int));
                    /* Keep CMSG_SPACE — do not narrow below CMSG_ALIGN(cmsg_len). */
                    msg.msg_controllen = sizeof(cmsg_buf.buf);
                    (void)sendmsg(conn, &msg, 0);
                }
                close(conn);
            }
            close(rlisten);
            _exit(0);
        }

        {
            int got = -1;
            int status = 0;
            /* hypty_recv_fd connects as client — race: wait until sock exists. */
            usleep(50 * 1000);
            if (hypty_recv_fd(rpath, &got) == 0) {
                /* Must reject pipe. */
                if (got >= 0)
                    close(got);
                waitpid(child, NULL, 0);
                close(rlisten);
                goto out;
            }
            waitpid(child, &status, 0);
            close(rlisten);
            (void)unlink(rpath);
            (void)status;
        }
    }

    /* Recv path: send fd 0 (stdin) via raw sendmsg. hypty_recv_fd must
     * reject before close/adopt so helper stdin stays open. */
    {
        char zpath[sizeof(path)];
        int zlisten = -1;
        pid_t child;
        int stdin_flags;

        stdin_flags = fcntl(STDIN_FILENO, F_GETFD);
        if (stdin_flags < 0)
            goto out;

        if ((size_t)snprintf(zpath, sizeof(zpath), "%s/z.sock", dir) >= sizeof(zpath))
            goto out;
        zlisten = socket(AF_UNIX, SOCK_STREAM, 0);
        if (zlisten < 0)
            goto out;
        memset(&addr, 0, sizeof(addr));
        addr.sun_family = AF_UNIX;
        plen = strlen(zpath);
        memcpy(addr.sun_path, zpath, plen + 1);
#if defined(__APPLE__)
        addr.sun_len = (unsigned char)(offsetof(struct sockaddr_un, sun_path) + plen + 1);
#endif
        (void)unlink(zpath);
        if (bind(zlisten, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
            close(zlisten);
            goto out;
        }
        if (chmod(zpath, 0600) != 0) {
            close(zlisten);
            goto out;
        }
        if (listen(zlisten, 1) != 0) {
            close(zlisten);
            goto out;
        }

        child = fork();
        if (child < 0) {
            close(zlisten);
            goto out;
        }
        if (child == 0) {
            int conn = accept(zlisten, NULL, NULL);
            if (conn >= 0) {
                struct msghdr msg;
                struct iovec iov;
                char dummy = 0;
                union {
                    struct cmsghdr align;
                    char buf[CMSG_SPACE(sizeof(int))];
                } cmsg_buf;
                struct cmsghdr *cmsg;
                int send_fd = STDIN_FILENO;

                memset(&msg, 0, sizeof(msg));
                memset(&cmsg_buf, 0, sizeof(cmsg_buf));
                iov.iov_base = &dummy;
                iov.iov_len = 1;
                msg.msg_iov = &iov;
                msg.msg_iovlen = 1;
                msg.msg_control = cmsg_buf.buf;
                msg.msg_controllen = sizeof(cmsg_buf.buf);
                cmsg = CMSG_FIRSTHDR(&msg);
                if (cmsg) {
                    cmsg->cmsg_level = SOL_SOCKET;
                    cmsg->cmsg_type = SCM_RIGHTS;
                    cmsg->cmsg_len = CMSG_LEN(sizeof(int));
                    memcpy(CMSG_DATA(cmsg), &send_fd, sizeof(int));
                    /* Keep CMSG_SPACE — do not narrow below CMSG_ALIGN(cmsg_len). */
                    msg.msg_controllen = sizeof(cmsg_buf.buf);
                    (void)sendmsg(conn, &msg, 0);
                }
                close(conn);
            }
            close(zlisten);
            _exit(0);
        }

        {
            int got = -1;
            int status = 0;
            usleep(50 * 1000);
            if (hypty_recv_fd(zpath, &got) == 0) {
                if (got >= 0)
                    close(got);
                waitpid(child, NULL, 0);
                close(zlisten);
                goto out;
            }
            if (got > 0)
                close(got);
            if (fcntl(STDIN_FILENO, F_GETFD) < 0) {
                waitpid(child, NULL, 0);
                close(zlisten);
                goto out;
            }
            waitpid(child, &status, 0);
            close(zlisten);
            (void)unlink(zpath);
            (void)status;
        }
    }

    rc = 0;
out:
    if (pipefd[0] >= 0)
        close(pipefd[0]);
    if (pipefd[1] >= 0)
        close(pipefd[1]);
    if (master >= 0)
        close(master);
    if (listen_fd >= 0)
        close(listen_fd);
    if (dir[0] == '/') {
        char p[sizeof(path)];
        snprintf(p, sizeof(p), "%s/s.sock", dir);
        (void)unlink(p);
        snprintf(p, sizeof(p), "%s/r.sock", dir);
        (void)unlink(p);
        snprintf(p, sizeof(p), "%s/z.sock", dir);
        (void)unlink(p);
        (void)rmdir(dir);
    }
    return rc;
#else
    (void)master;
    (void)path;
    (void)listen_fd;
    (void)addr;
    (void)plen;
    close(pipefd[0]);
    close(pipefd[1]);
    return 0; /* non-Unix build skip */
#endif
}
