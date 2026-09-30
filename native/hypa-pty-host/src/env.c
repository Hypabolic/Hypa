#include "env.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

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

void hypty_env_free(char **envp)
{
    size_t i;
    if (!envp)
        return;
    for (i = 0; envp[i] != NULL; i++)
        free(envp[i]);
    free(envp);
}

int hypty_env_from_spawn(
    const uint8_t *payload,
    uint32_t payload_len,
    uint32_t env_offset,
    char ***envp_out,
    size_t *count_out)
{
    uint32_t envc;
    uint32_t off;
    size_t i;
    char **envp;

    if (!payload || !envp_out)
        return -1;
    *envp_out = NULL;
    if (count_out)
        *count_out = 0;

    if (!remaining_ok(env_offset, 4, payload_len))
        return -1;
    envc = read_u32_le(payload + env_offset);
    if (envc > 4096)
        return -1;
    off = env_offset + 4;

    envp = (char **)calloc((size_t)envc + 1, sizeof(char *));
    if (!envp)
        return -1;

    for (i = 0; i < (size_t)envc; i++) {
        uint32_t klen, vlen;
        size_t entry_sz;
        char *entry;
        const uint8_t *key;

        if (!remaining_ok(off, 4, payload_len))
            goto fail;
        klen = read_u32_le(payload + off);
        off += 4;
        if (!remaining_ok(off, klen, payload_len))
            goto fail;
        key = payload + off;
        off += klen;

        if (!remaining_ok(off, 4, payload_len))
            goto fail;
        vlen = read_u32_le(payload + off);
        off += 4;
        if (!remaining_ok(off, vlen, payload_len))
            goto fail;

        /* KEY=VAL\0. Remaining-bytes checks above bound klen/vlen. */
        entry_sz = (size_t)klen + 1 + (size_t)vlen + 1;
        entry = (char *)malloc(entry_sz);
        if (!entry)
            goto fail;
        memcpy(entry, key, klen);
        entry[klen] = '=';
        memcpy(entry + klen + 1, payload + off, vlen);
        entry[klen + 1 + vlen] = '\0';
        off += vlen;
        envp[i] = entry;
    }
    envp[envc] = NULL;
    *envp_out = envp;
    if (count_out)
        *count_out = (size_t)envc;
    return 0;

fail:
    hypty_env_free(envp);
    return -1;
}
