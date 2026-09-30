#ifndef HYPTY_ENV_H
#define HYPTY_ENV_H

#include <stddef.h>
#include <stdint.h>

/* Build a NULL-terminated envp array from Spawn payload env pairs.
 * *envp is malloc'd array of malloc'd "KEY=VAL" strings; free with hypty_env_free.
 */
int hypty_env_from_spawn(
    const uint8_t *payload,
    uint32_t payload_len,
    uint32_t env_offset,
    char ***envp_out,
    size_t *count_out);

void hypty_env_free(char **envp);

#endif /* HYPTY_ENV_H */
