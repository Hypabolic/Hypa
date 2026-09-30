#ifndef HYPTY_FDPASS_H
#define HYPTY_FDPASS_H

#include <stdint.h>

/* AF_UNIX SCM_RIGHTS send/recv of a single PTY master file descriptor.
 * path must be absolute, private (exact 0600 socket in exact 0700 dir owned
 * by euid). Peer credentials must match euid. Received FDs must be PTY masters
 * and are marked FD_CLOEXEC. Returns 0 on success, -1 on error.
 */
int hypty_send_fd(const char *path, int fd);
int hypty_recv_fd(const char *path, int *fd_out);

/* Reject relative / empty paths. Returns 0 if path looks safe enough. */
int hypty_path_is_absolute_uds(const char *path, uint32_t len);

#endif /* HYPTY_FDPASS_H */
