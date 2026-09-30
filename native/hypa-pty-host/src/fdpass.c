/*
 * AF_UNIX SCM_RIGHTS helpers for same-host handoff.
 * Managed process binds/listens; helper connects and sendmsg/recvmsg.
 *
 * Security (same-user model):
 *  - path must be absolute
 *  - path must be a socket inode owned by our euid with exact mode 0600
 *  - parent directory owned by our euid with exact mode 0700
 *  - peer credentials on the connected socket must match our euid
 *  - received FDs must be PTY masters; FD_CLOEXEC is set on ownership paths
 */

/* Expose SO_PEERCRED / ptsname on glibc with -std=c11. */
#if !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif

#include "fdpass.h"

#include <errno.h>
#include <fcntl.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/un.h>
#include <unistd.h>

#if defined(__linux__)
/* Portable ucred when headers hide it under feature macros. */
#ifndef SO_PEERCRED
#define SO_PEERCRED 17
#endif
struct hypty_ucred {
    pid_t pid;
    uid_t uid;
    gid_t gid;
};
#endif

int hypty_path_is_absolute_uds(const char *path, uint32_t len)
{
    if (!path || len == 0 || len >= sizeof(((struct sockaddr_un *)0)->sun_path))
        return 0;
    if (path[0] != '/')
        return 0;
    /* Reject embedded NUL in declared length. */
    if (memchr(path, '\0', len) != NULL)
        return 0;
    return 1;
}

static int set_cloexec(int fd)
{
    int flags;

    if (fd < 0)
        return -1;
    flags = fcntl(fd, F_GETFD);
    if (flags < 0)
        return -1;
    if (flags & FD_CLOEXEC)
        return 0;
    if (fcntl(fd, F_SETFD, flags | FD_CLOEXEC) < 0)
        return -1;
    return 0;
}

/* True when fd is a PTY master (not a pipe/file/slave-only tty). */
static int is_pty_master(int fd)
{
#if defined(__linux__)
    unsigned int ptyno = 0;
    return ioctl(fd, TIOCGPTN, &ptyno) == 0;
#elif defined(__APPLE__)
    char name[128];
#if defined(__DARWIN_C_LEVEL) && __DARWIN_C_LEVEL >= 200809L
    return ptsname_r(fd, name, sizeof(name)) == 0;
#else
    /* ptsname is not thread-safe but helper is single-threaded. */
    return ptsname(fd) != NULL;
#endif
#else
    (void)fd;
    return 0;
#endif
}

/*
 * Exact private path: socket 0600 + parent dir 0700, both euid-owned.
 * Rejects owner-mode deviations (e.g. socket 0000/0400/0700) and
 * non-private parents (e.g. 0755/0711). TOCTOU remains for the path itself;
 * peer-cred on the connected socket closes the identity gap after connect.
 */
static int path_is_private_socket(const char *path)
{
    struct stat st;
    char parent[sizeof(((struct sockaddr_un *)0)->sun_path) + 8];
    size_t plen;
    char *slash;

    if (!path || path[0] != '/')
        return 0;
    if (lstat(path, &st) != 0)
        return 0;
    if (!S_ISSOCK(st.st_mode))
        return 0;
    if (st.st_uid != geteuid())
        return 0;
    /* Exact 0600 — not merely "no group/other bits". */
    if ((st.st_mode & 0777) != 0600)
        return 0;

    /* Parent directory: exact private mode 0700, owned by euid. */
    plen = strlen(path);
    if (plen >= sizeof(parent))
        return 0;
    memcpy(parent, path, plen + 1);
    slash = strrchr(parent, '/');
    if (!slash)
        return 0;
    if (slash == parent)
        slash[1] = '\0'; /* "/" */
    else
        *slash = '\0';

    if (lstat(parent, &st) != 0)
        return 0;
    if (!S_ISDIR(st.st_mode))
        return 0;
    if (st.st_uid != geteuid())
        return 0;
    if ((st.st_mode & 0777) != 0700)
        return 0;

    return 1;
}

static int peer_uid_is_self(int sock)
{
    uid_t self = geteuid();

#if defined(__linux__)
    struct hypty_ucred cred;
    socklen_t len = (socklen_t)sizeof(cred);
    memset(&cred, 0, sizeof(cred));
    if (getsockopt(sock, SOL_SOCKET, SO_PEERCRED, &cred, &len) != 0)
        return 0;
    return cred.uid == self;
#elif defined(__APPLE__)
    uid_t euid = (uid_t)-1;
    gid_t egid = (gid_t)-1;
    if (getpeereid(sock, &euid, &egid) != 0)
        return 0;
    return euid == self;
#else
    (void)sock;
    (void)self;
    return 0;
#endif
}

static int connect_unix(const char *path)
{
    int fd;
    struct sockaddr_un addr;
    size_t plen;

    if (!path || path[0] != '/')
        return -1;

    plen = strlen(path);
    if (plen == 0 || plen >= sizeof(addr.sun_path))
        return -1;

    if (!path_is_private_socket(path)) {
        errno = EACCES;
        return -1;
    }

    fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0)
        return -1;

    if (set_cloexec(fd) != 0) {
        int e = errno;
        close(fd);
        errno = e;
        return -1;
    }

    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    memcpy(addr.sun_path, path, plen);
    addr.sun_path[plen] = '\0';
#if defined(__APPLE__)
    addr.sun_len = (unsigned char)(offsetof(struct sockaddr_un, sun_path) + plen + 1);
#endif

    if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
        int e = errno;
        close(fd);
        errno = e;
        return -1;
    }

    if (!peer_uid_is_self(fd)) {
        close(fd);
        errno = EPERM;
        return -1;
    }

    return fd;
}

int hypty_send_fd(const char *path, int send_fd)
{
    int sock = -1;
    struct msghdr msg;
    struct iovec iov;
    char dummy = 0;
    union {
        struct cmsghdr align;
        char buf[CMSG_SPACE(sizeof(int))];
    } cmsg_buf;
    struct cmsghdr *cmsg;
    int rc = -1;

    if (send_fd < 0 || !path)
        return -1;

    /* Ownership hygiene on the master we export. */
    if (set_cloexec(send_fd) != 0)
        return -1;
    if (!is_pty_master(send_fd)) {
        errno = EINVAL;
        return -1;
    }

    sock = connect_unix(path);
    if (sock < 0)
        return -1;

    memset(&msg, 0, sizeof(msg));
    memset(&cmsg_buf, 0, sizeof(cmsg_buf));
    iov.iov_base = &dummy;
    iov.iov_len = 1;
    msg.msg_iov = &iov;
    msg.msg_iovlen = 1;
    msg.msg_control = cmsg_buf.buf;
    msg.msg_controllen = sizeof(cmsg_buf.buf);

    cmsg = CMSG_FIRSTHDR(&msg);
    if (!cmsg)
        goto out;
    cmsg->cmsg_level = SOL_SOCKET;
    cmsg->cmsg_type = SCM_RIGHTS;
    cmsg->cmsg_len = CMSG_LEN(sizeof(int));
    memcpy(CMSG_DATA(cmsg), &send_fd, sizeof(int));
    /* Keep CMSG_SPACE. Narrowing to cmsg_len can make
     * msg_controllen < CMSG_ALIGN(cmsg_len) and panic XNU. */
    msg.msg_controllen = sizeof(cmsg_buf.buf);

    if (sendmsg(sock, &msg, 0) < 0)
        goto out;
    rc = 0;

out:
    if (sock >= 0)
        close(sock);
    return rc;
}

int hypty_recv_fd(const char *path, int *fd_out)
{
    int sock = -1;
    struct msghdr msg;
    struct iovec iov;
    char dummy = 0;
    union {
        struct cmsghdr align;
        char buf[CMSG_SPACE(sizeof(int))];
    } cmsg_buf;
    struct cmsghdr *cmsg;
    int got_fd = -1;
    int rc = -1;

    if (!fd_out || !path)
        return -1;
    *fd_out = -1;

    sock = connect_unix(path);
    if (sock < 0)
        return -1;

    memset(&msg, 0, sizeof(msg));
    memset(&cmsg_buf, 0, sizeof(cmsg_buf));
    iov.iov_base = &dummy;
    iov.iov_len = 1;
    msg.msg_iov = &iov;
    msg.msg_iovlen = 1;
    msg.msg_control = cmsg_buf.buf;
    msg.msg_controllen = sizeof(cmsg_buf.buf);

    if (recvmsg(sock, &msg, 0) < 0)
        goto out;

    for (cmsg = CMSG_FIRSTHDR(&msg); cmsg != NULL; cmsg = CMSG_NXTHDR(&msg, cmsg)) {
        if (cmsg->cmsg_level == SOL_SOCKET && cmsg->cmsg_type == SCM_RIGHTS) {
            if (cmsg->cmsg_len >= CMSG_LEN(sizeof(int))) {
                memcpy(&got_fd, CMSG_DATA(cmsg), sizeof(int));
                break;
            }
        }
    }

    /* fd 0 is stdin. Never treat it as a passed master; close(0) would
     * drop helper stdin when a valid-length cmsg delivers fd 0 and
     * is_pty_master fails. */
    if (got_fd <= 0)
        goto out;

    /* Reject arbitrary non-PTY adoption; harden CLOEXEC on ownership receive. */
    if (!is_pty_master(got_fd)) {
        close(got_fd);
        got_fd = -1;
        errno = EINVAL;
        goto out;
    }
    if (set_cloexec(got_fd) != 0) {
        int e = errno;
        close(got_fd);
        got_fd = -1;
        errno = e;
        goto out;
    }

    *fd_out = got_fd;
    rc = 0;

out:
    if (sock >= 0)
        close(sock);
    return rc;
}
