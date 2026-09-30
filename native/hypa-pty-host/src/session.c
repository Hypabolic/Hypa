#include "session.h"
#include "env.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <util.h>
#else
#include <pty.h>
#endif

/* Prefer system headers for TIOCSCTTY / TIOCSWINSZ on each OS. */

static uint16_t read_u16_le(const uint8_t *p)
{
    return (uint16_t)(p[0] | (p[1] << 8));
}

static uint32_t read_u32_le(const uint8_t *p)
{
    return (uint32_t)p[0]
        | ((uint32_t)p[1] << 8)
        | ((uint32_t)p[2] << 16)
        | ((uint32_t)p[3] << 24);
}

/* Remaining-bytes check. Rejects uint32 wrap (off + need). */
static int remaining_ok(uint32_t off, uint32_t need, uint32_t len)
{
    return off <= len && need <= len - off;
}

static int set_nonblock(int fd)
{
    int flags;
    if (fd < 0)
        return -1;
    flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0)
        return -1;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

static char *dup_cstr(const uint8_t *p, uint32_t len)
{
    /* uint32 length already bounds-checked against remaining payload. */
    char *s = (char *)malloc((size_t)len + 1);
    if (!s)
        return NULL;
    memcpy(s, p, len);
    s[len] = '\0';
    return s;
}

static void free_argv(char **argv, size_t n)
{
    size_t i;
    if (!argv)
        return;
    for (i = 0; i < n; i++)
        free(argv[i]);
    free(argv);
}

int hypty_session_spawn(struct hypty_session *session, const uint8_t *payload, uint32_t len)
{
    uint16_t cols, rows;
    uint32_t off;
    uint32_t cwd_len, argc;
    char *cwd = NULL;
    char **argv = NULL;
    char **envp = NULL;
    size_t i;
    int master = -1, slave = -1;
    pid_t pid;
    struct winsize ws;

    if (!session || !payload || len < 8)
        return -1;

    memset(session, 0, sizeof(*session));
    session->master_fd = -1;
    session->child_pid = -1;

    cols = read_u16_le(payload);
    rows = read_u16_le(payload + 2);
    if (cols == 0)
        cols = 80;
    if (rows == 0)
        rows = 24;
    off = 4;

    if (!remaining_ok(off, 4, len))
        return -1;
    cwd_len = read_u32_le(payload + off);
    off += 4;
    if (!remaining_ok(off, cwd_len, len))
        return -1;
    cwd = dup_cstr(payload + off, cwd_len);
    if (!cwd)
        return -1;
    off += cwd_len;

    if (!remaining_ok(off, 4, len))
        goto fail;
    argc = read_u32_le(payload + off);
    off += 4;
    if (argc == 0 || argc > 1024)
        goto fail;

    argv = (char **)calloc((size_t)argc + 1, sizeof(char *));
    if (!argv)
        goto fail;
    for (i = 0; i < (size_t)argc; i++) {
        uint32_t alen;
        if (!remaining_ok(off, 4, len))
            goto fail;
        alen = read_u32_le(payload + off);
        off += 4;
        if (!remaining_ok(off, alen, len))
            goto fail;
        argv[i] = dup_cstr(payload + off, alen);
        if (!argv[i])
            goto fail;
        off += alen;
    }
    argv[argc] = NULL;

    /* Shared Spawn env parser (single owner of KEY=VAL layout). */
    if (hypty_env_from_spawn(payload, len, off, &envp, NULL) != 0)
        goto fail;

    memset(&ws, 0, sizeof(ws));
    ws.ws_col = cols;
    ws.ws_row = rows;

    if (openpty(&master, &slave, NULL, NULL, &ws) != 0)
        goto fail;

    /* Drain must not block when a grandchild still holds the slave. */
    if (set_nonblock(master) != 0)
        goto fail_fds;

    pid = fork();
    if (pid < 0)
        goto fail_fds;

    if (pid == 0) {
        /* Child */
        close(master);
        if (setsid() < 0)
            _exit(127);

        if (ioctl(slave, TIOCSCTTY, 0) != 0) {
            /* Best-effort on platforms where openpty already set CTTY. */
        }

        if (dup2(slave, STDIN_FILENO) < 0
            || dup2(slave, STDOUT_FILENO) < 0
            || dup2(slave, STDERR_FILENO) < 0)
            _exit(127);
        if (slave > STDERR_FILENO)
            close(slave);

        if (cwd[0] != '\0' && chdir(cwd) != 0)
            _exit(127);

        /* Parent keeps SIGPIPE=IGN. Restore default so the child dies on SIGPIPE. */
        (void)signal(SIGPIPE, SIG_DFL);
        execve(argv[0], argv, envp);
        _exit(127);
    }

    /* Parent */
    close(slave);
    free(cwd);
    free_argv(argv, (size_t)argc);
    hypty_env_free(envp);

    session->master_fd = master;
    session->child_pid = pid;
    session->child_exited = 0;
    session->exit_code = 0;
    session->is_adopted = 0;
    return 0;

fail_fds:
    if (master >= 0)
        close(master);
    if (slave >= 0)
        close(slave);
fail:
    free(cwd);
    if (argv)
        free_argv(argv, (size_t)argc);
    hypty_env_free(envp);
    return -1;
}

void hypty_session_resize(struct hypty_session *session, uint16_t cols, uint16_t rows)
{
    struct winsize ws;
    if (!session || session->master_fd < 0)
        return;
    memset(&ws, 0, sizeof(ws));
    ws.ws_col = cols == 0 ? 80 : cols;
    ws.ws_row = rows == 0 ? 24 : rows;
    (void)ioctl(session->master_fd, TIOCSWINSZ, &ws);
}

void hypty_session_signal_group(struct hypty_session *session, int sig)
{
    if (!session || session->child_pid <= 0)
        return;
    /*
     * Negative pid = process group (child called setsid).
     * Do not gate on child_exited: the session leader can exit while
     * SIGTERM-immune (or slow) descendants remain in the group.
     */
    if (kill(-session->child_pid, sig) == 0)
        return;
    /*
     * Leader-only fallback only when the leader is still unreaped and this
     * is not SIGKILL. After hard-kill / reaping the leader pid may be reused.
     */
    if (!session->child_exited && sig != SIGKILL)
        (void)kill(session->child_pid, sig);
}

void hypty_session_terminate(struct hypty_session *session, int grace_ms)
{
    pid_t pgid;
    int waited = 0;
    int hard_waited = 0;
    /* Cap post-SIGKILL reap so a stuck PTY exit path cannot hang the helper. */
    const int hard_reap_ms = 200;

    if (!session || session->child_pid <= 0)
        return;

    /* Save pgid before any reap (leader pid == session/group id after setsid). */
    pgid = session->child_pid;

    /* Soft stop the whole group. */
    hypty_session_signal_group(session, SIGTERM);

    /*
     * Close the master early. A chatty child blocked on a full slave write
     * (or stuck in session-leader tty exit, macOS state E) will not finish
     * dying while the master stays open and unread. Closing gives EIO/HUP
     * and unblocks exit so waitpid can complete.
     */
    hypty_session_close_master(session);

    while (waited < grace_ms) {
        if (hypty_session_try_reap(session) == 1)
            break;
        usleep(20 * 1000);
        waited += 20;
    }

    /*
     * Always hard-kill the process group after grace. The leader may already
     * be reaped while SIGTERM-immune descendants remain. Do not gate on
     * child_exited. Never fall back to kill(leader): pid may be reusable.
     */
    if (pgid > 0)
        (void)kill(-pgid, SIGKILL);

    /*
     * Collect leader status with a bounded WNOHANG loop — never blocking
     * waitpid(…, 0). A permanent hang here would prevent Exit frames and
     * stall every managed dispose (CloseExitWait → synthetic -1).
     */
    while (!session->child_exited && hard_waited < hard_reap_ms) {
        if (hypty_session_try_reap(session) == 1)
            break;
        usleep(10 * 1000);
        hard_waited += 10;
    }
    if (!session->child_exited) {
        /* Last chance; still non-blocking. */
        (void)hypty_session_try_reap(session);
        if (!session->child_exited)
            session->exit_code = -1;
    }
}

int hypty_session_try_reap(struct hypty_session *session)
{
    int status = 0;
    pid_t r;
    if (!session || session->child_pid <= 0)
        return -1;
    if (session->child_exited)
        return 1;

    /*
     * After H2 Adopt the helper is not the parent (CloseOldOwner reparented
     * the child). waitpid returns ECHILD forever — probe with kill(pid, 0).
     */
    if (session->is_adopted) {
        if (kill(session->child_pid, 0) == 0)
            return 0; /* still alive */
        if (errno == ESRCH) {
            /* Status unknown without parenthood. */
            hypty_session_mark_exited(session, -1);
            return 1;
        }
        /* EPERM etc.: treat as still alive. */
        return 0;
    }

    r = waitpid(session->child_pid, &status, WNOHANG);
    if (r == 0)
        return 0;
    if (r < 0) {
        if (errno == ECHILD) {
            /* Lost parenthood unexpectedly — fall back to liveness probe. */
            if (kill(session->child_pid, 0) == 0)
                return 0;
            if (errno == ESRCH) {
                hypty_session_mark_exited(session, -1);
                return 1;
            }
            return 0;
        }
        return -1;
    }
    if (r != session->child_pid)
        return -1;
    session->child_exited = 1;
    if (WIFEXITED(status))
        session->exit_code = WEXITSTATUS(status);
    else if (WIFSIGNALED(status))
        session->exit_code = 128 + WTERMSIG(status);
    else
        session->exit_code = -1;
    return 1;
}

void hypty_session_mark_exited(struct hypty_session *session, int exit_code)
{
    if (!session || session->child_exited)
        return;
    session->child_exited = 1;
    session->exit_code = exit_code;
}

void hypty_session_close_master(struct hypty_session *session)
{
    if (!session)
        return;
    if (session->master_fd >= 0) {
        close(session->master_fd);
        session->master_fd = -1;
    }
}

int hypty_write_master_all(int master_fd, const uint8_t *data, uint32_t len,
    int control_fd, uint32_t *written_out,
    int (*pump)(int master_fd, void *ctx), void *ctx)
{
    uint32_t sent = 0;

    if (written_out)
        *written_out = 0;
    if (master_fd < 0 || !data)
        return -1;
    if (len == 0)
        return 0;

    while (sent < len) {
        struct pollfd pfds[2];
        nfds_t nfds;
        int stdin_i;
        int master_i;
        int pr;
        ssize_t w = write(master_fd, data + sent, (size_t)(len - sent));

        if (w > 0) {
            sent += (uint32_t)w;
            if (written_out)
                *written_out = sent;
            continue;
        }
        if (w < 0 && errno == EINTR)
            continue;
        if (!(w < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)))
            return -1;

        /* EAGAIN: wait for POLLOUT. stdin Close/Signal must still win. */
        nfds = 0;
        stdin_i = -1;
        master_i = -1;
        if (control_fd >= 0) {
            pfds[nfds].fd = control_fd;
            pfds[nfds].events = POLLIN;
            stdin_i = (int)nfds;
            nfds++;
        }
        pfds[nfds].fd = master_fd;
        pfds[nfds].events = (short)(POLLOUT | (pump ? POLLIN : 0));
        master_i = (int)nfds;
        nfds++;

        pr = poll(pfds, nfds, -1);
        if (pr < 0) {
            if (errno == EINTR)
                continue;
            return -1;
        }
        if (stdin_i >= 0
            && (pfds[stdin_i].revents & (POLLIN | POLLHUP | POLLERR))) {
            if (written_out)
                *written_out = sent;
            return 1;
        }
        if (master_i >= 0 && pump && (pfds[master_i].revents & POLLIN)) {
            if (pump(master_fd, ctx) < 0)
                return -1;
        }
        if (master_i >= 0
            && (pfds[master_i].revents & (POLLHUP | POLLERR))
            && !(pfds[master_i].revents & POLLOUT)) {
            return -1;
        }
    }
    return 0;
}
