#ifndef HYPTY_IPC_H
#define HYPTY_IPC_H

#include <stddef.h>
#include <stdint.h>

#include "pty_host_ipc.h"

struct hypty_frame {
    uint8_t type;
    const uint8_t *payload;
    uint32_t payload_len;
};

/* Read one complete frame from fd into *out_buf (malloc'd body: type+payload).
 * *frame views into *out_buf. Caller frees *out_buf. Returns 0 on success, -1 on error/EOF.
 */
int hypty_frame_read(int fd, struct hypty_frame *frame, uint8_t **out_buf);

/* Write one frame to fd. Returns 0 on success. */
int hypty_frame_write(int fd, uint8_t type, const void *payload, uint32_t payload_len);

int hypty_write_hello(int fd);
int hypty_write_error(int fd, int32_t code, const char *msg);
int hypty_write_exit(int fd, int32_t exit_code, int32_t pid);
int hypty_write_output(int fd, const void *data, uint32_t len);
/* after successful Spawn — payload is int32 LE child_pid.*/
int hypty_write_spawned(int fd, int32_t child_pid);

/* Parse helpers; return 0 on success. */
int hypty_parse_hello(const uint8_t *payload, uint32_t len, uint16_t *minor_out);

#endif /* HYPTY_IPC_H */
