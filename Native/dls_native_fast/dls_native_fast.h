#pragma once
#include <stdint.h>

typedef struct {
    uint32_t op;
    int32_t in_ref[8];
    int32_t out_ref[8];
} dls_native_node;

void *dls_native_create_program(const dls_native_node *nodes, int32_t node_count,
                                const int32_t *output_refs, int32_t output_count,
                                int32_t scratch_count);
void dls_native_destroy_program(void *handle);
int32_t dls_native_eval(void *handle, uint32_t *scratch, int32_t scratch_count,
                        uint32_t *outputs, int32_t output_count);
uint32_t dls_native_abi_version(void);
