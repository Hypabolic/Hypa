/* hypa-pty-host IPC frame type codes and limits.
 * Normative text: docs/plans/AgentRuntime/pty-host-ipc.md
 */
#ifndef HYPTY_PTY_HOST_IPC_H
#define HYPTY_PTY_HOST_IPC_H

#include <stdint.h>

#define HYPTY_MAX_BODY        (1024u * 1024u) /* 1 MiB */
#define HYPTY_MAX_CHUNK       (64u * 1024u)   /* 64 KiB */
#define HYPTY_PROTOCOL_MINOR  0

#define HYPTY_FRAME_HELLO           1
#define HYPTY_FRAME_SPAWN           2
#define HYPTY_FRAME_OUTPUT          3
#define HYPTY_FRAME_INPUT           4
#define HYPTY_FRAME_RESIZE          5
#define HYPTY_FRAME_SIGNAL          6
#define HYPTY_FRAME_EXIT            7
#define HYPTY_FRAME_CLOSE           8
#define HYPTY_FRAME_ERROR           9
/* same-host SCM_RIGHTS handoff (FD path embedded; stdio never carries FDs)*/
#define HYPTY_FRAME_PAUSE_OUTPUT    10
#define HYPTY_FRAME_ADOPT           11
#define HYPTY_FRAME_ADOPTED         12
#define HYPTY_FRAME_CLOSE_OLD_OWNER 13
/* child pid ack after successful Spawn (payload: int32 LE pid)*/
#define HYPTY_FRAME_SPAWNED         14
/* unpause after failed export (clear pause identity; never kill child)*/
#define HYPTY_FRAME_RESUME_HANDOFF  15
/* new helper aborts after Adopt — close master only; never kill child*/
#define HYPTY_FRAME_RELEASE_ADOPTED 16

/* "HYPTY1\0" */
static const uint8_t hypty_hello_magic[7] = {
    'H', 'Y', 'P', 'T', 'Y', '1', 0
};

enum {
    HYPTY_ERR_PROTOCOL = 1,
    HYPTY_ERR_SPAWN    = 2,
    HYPTY_ERR_IO       = 3,
    HYPTY_ERR_INTERNAL = 4,
    HYPTY_ERR_HANDOFF  = 5
};

#endif /* HYPTY_PTY_HOST_IPC_H */
