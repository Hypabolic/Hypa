#include "ipc.h"

#include <errno.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static int read_full(int fd, void *buf, size_t n)
{
    uint8_t *p = (uint8_t *)buf;
    size_t got = 0;
    while (got < n) {
        ssize_t r = read(fd, p + got, n - got);
        if (r == 0)
            return -1; /* EOF */
        if (r < 0) {
            if (errno == EINTR)
                continue;
            return -1;
        }
        got += (size_t)r;
    }
    return 0;
}

static int write_full(int fd, const void *buf, size_t n)
{
    const uint8_t *p = (const uint8_t *)buf;
    size_t sent = 0;
    while (sent < n) {
        ssize_t w = write(fd, p + sent, n - sent);
        if (w < 0) {
            if (errno == EINTR)
                continue;
            return -1;
        }
        sent += (size_t)w;
    }
    return 0;
}

static uint32_t read_u32_le(const uint8_t *p)
{
    return (uint32_t)p[0]
        | ((uint32_t)p[1] << 8)
        | ((uint32_t)p[2] << 16)
        | ((uint32_t)p[3] << 24);
}

static void write_u32_le(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xff);
    p[1] = (uint8_t)((v >> 8) & 0xff);
    p[2] = (uint8_t)((v >> 16) & 0xff);
    p[3] = (uint8_t)((v >> 24) & 0xff);
}

static uint16_t read_u16_le(const uint8_t *p)
{
    return (uint16_t)(p[0] | (p[1] << 8));
}

static void write_u16_le(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)(v & 0xff);
    p[1] = (uint8_t)((v >> 8) & 0xff);
}

static void write_i32_le(uint8_t *p, int32_t v)
{
    write_u32_le(p, (uint32_t)v);
}

int hypty_frame_read(int fd, struct hypty_frame *frame, uint8_t **out_buf)
{
    uint8_t lenbuf[4];
    uint32_t body_len;
    uint8_t *body;

    if (!frame || !out_buf)
        return -1;
    *out_buf = NULL;

    if (read_full(fd, lenbuf, 4) != 0)
        return -1;
    body_len = read_u32_le(lenbuf);
    if (body_len == 0 || body_len > HYPTY_MAX_BODY)
        return -1;

    body = (uint8_t *)malloc(body_len);
    if (!body)
        return -1;
    if (read_full(fd, body, body_len) != 0) {
        free(body);
        return -1;
    }

    frame->type = body[0];
    frame->payload = body_len > 1 ? body + 1 : NULL;
    frame->payload_len = body_len - 1;
    *out_buf = body;
    return 0;
}

int hypty_frame_write(int fd, uint8_t type, const void *payload, uint32_t payload_len)
{
    uint8_t hdr[5];
    uint32_t body_len;

    if (payload_len > HYPTY_MAX_BODY - 1)
        return -1;
    if ((type == HYPTY_FRAME_OUTPUT || type == HYPTY_FRAME_INPUT)
        && payload_len > HYPTY_MAX_CHUNK)
        return -1;

    body_len = 1u + payload_len;
    write_u32_le(hdr, body_len);
    hdr[4] = type;
    if (write_full(fd, hdr, 5) != 0)
        return -1;
    if (payload_len > 0 && payload != NULL) {
        if (write_full(fd, payload, payload_len) != 0)
            return -1;
    }
    return 0;
}

int hypty_write_hello(int fd)
{
    uint8_t payload[9];
    memcpy(payload, hypty_hello_magic, 7);
    write_u16_le(payload + 7, HYPTY_PROTOCOL_MINOR);
    return hypty_frame_write(fd, HYPTY_FRAME_HELLO, payload, 9);
}

int hypty_write_error(int fd, int32_t code, const char *msg)
{
    size_t mlen;
    uint8_t *payload;
    int rc;

    if (!msg)
        msg = "";
    mlen = strlen(msg);
    if (mlen > 512)
        mlen = 512;
    payload = (uint8_t *)malloc(8 + mlen);
    if (!payload)
        return -1;
    write_i32_le(payload, code);
    write_u32_le(payload + 4, (uint32_t)mlen);
    if (mlen)
        memcpy(payload + 8, msg, mlen);
    rc = hypty_frame_write(fd, HYPTY_FRAME_ERROR, payload, (uint32_t)(8 + mlen));
    free(payload);
    return rc;
}

int hypty_write_exit(int fd, int32_t exit_code, int32_t pid)
{
    uint8_t payload[8];
    write_i32_le(payload, exit_code);
    write_i32_le(payload + 4, pid);
    return hypty_frame_write(fd, HYPTY_FRAME_EXIT, payload, 8);
}

int hypty_write_output(int fd, const void *data, uint32_t len)
{
    return hypty_frame_write(fd, HYPTY_FRAME_OUTPUT, data, len);
}

int hypty_write_spawned(int fd, int32_t child_pid)
{
    uint8_t payload[4];
    write_i32_le(payload, child_pid);
    return hypty_frame_write(fd, HYPTY_FRAME_SPAWNED, payload, 4);
}

int hypty_parse_hello(const uint8_t *payload, uint32_t len, uint16_t *minor_out)
{
    if (!payload || len < 9)
        return -1;
    if (memcmp(payload, hypty_hello_magic, 7) != 0)
        return -1;
    if (minor_out)
        *minor_out = read_u16_le(payload + 7);
    return 0;
}
