#ifndef HYPTY_SESSION_H
#define HYPTY_SESSION_H

#include <stdint.h>
#include <sys/types.h>

struct hypty_session {
    int master_fd;
    pid_t child_pid;
    int child_exited;
    int exit_code;
    /* 1 after H2 Adopt: helper is not the parent; waitpid is not valid. */
    int is_adopted;
    /*
     * 1 after master EOF/EIO while the child is still alive (adopted path).
     * Stops re-polling the drained master (busy-spin) without inventing Exit;
     * death is proven only by hypty_session_try_reap (kill(pid,0)).
     */
    int master_eof;
};

/* Parse Spawn payload and start child. Returns 0 on success. */
int hypty_session_spawn(struct hypty_session *session, const uint8_t *payload, uint32_t len);

void hypty_session_resize(struct hypty_session *session, uint16_t cols, uint16_t rows);

/* Signal the session process group. Not gated on child_exited (descendants may remain). */
void hypty_session_signal_group(struct hypty_session *session, int sig);

/* SIGTERM group → grace_ms → always group SIGKILL (even if leader already reaped). */
void hypty_session_terminate(struct hypty_session *session, int grace_ms);

/*
 * Observe child exit. Spawned sessions: non-blocking waitpid.
 * Adopted sessions: kill(pid,0) liveness probe (never waitpid — ECHILD).
 * Returns 1 if exited, 0 if running, -1 on error.
 */
int hypty_session_try_reap(struct hypty_session *session);

/*
 * Mark session dead after master EIO/EOF/HUP on an adopted (non-parent) session.
 * exit_code is -1 when the real status is unknown (no waitpid parenthood).
 */
void hypty_session_mark_exited(struct hypty_session *session, int exit_code);

void hypty_session_close_master(struct hypty_session *session);

/*
 * Write all of data to a non-blocking PTY master.
 * On EAGAIN/EWOULDBLOCK, poll master POLLOUT (and POLLIN when pump is set)
 * together with control_fd so Close/Signal on stdin can win.
 * Do not treat EAGAIN as end-of-input.
 *
 * pump: optional; when master is readable, call pump(master_fd, ctx).
 * Return 0 from pump to continue. Return <0 to abort.
 *
 * Returns:
 *   0 — every byte written
 *   1 — control_fd readable or HUP; *written_out is bytes already written
 *  -1 — fatal write/poll/pump error (not EAGAIN)
 */
int hypty_write_master_all(int master_fd, const uint8_t *data, uint32_t len,
    int control_fd, uint32_t *written_out,
    int (*pump)(int master_fd, void *ctx), void *ctx);

#endif /* HYPTY_SESSION_H */
