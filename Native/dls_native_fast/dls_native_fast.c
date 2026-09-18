#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define DLS_EXPORT __declspec(dllexport)
#else
#define DLS_EXPORT __attribute__((visibility("default")))
#endif

#define DLS_DISCONNECTED 0xFFFF0000u
#define DLS_SINGLE_BIT_MASK 0x00010001u

enum {
    DLS_OP_NAND = 1,
    DLS_OP_TRISTATE = 2,
    DLS_OP_SPLIT4_TO_1 = 3,
    DLS_OP_SPLIT8_TO_1 = 4,
    DLS_OP_MERGE1_TO_4 = 5,
    DLS_OP_MERGE1_TO_8 = 6,
    DLS_OP_MERGE4_TO_8 = 7,
    DLS_OP_SPLIT8_TO_4 = 8,
    DLS_OP_BUS = 9
};

typedef struct {
    uint32_t op;
    int32_t in_ref[8];
    int32_t out_ref[8];
} dls_native_node;

typedef struct {
    int32_t node_count;
    int32_t output_count;
    int32_t scratch_count;
    dls_native_node *nodes;
    int32_t *output_refs;
} dls_native_program;

static inline uint32_t load_ref(const uint32_t *scratch, int32_t ref)
{
    return ref >= 0 ? scratch[ref] : DLS_DISCONNECTED;
}

static inline void split_bit(uint32_t *scratch, int32_t out_ref, uint32_t value, int shift)
{
    scratch[out_ref] = (value >> shift) & DLS_SINGLE_BIT_MASK;
}

static inline uint32_t merge_bit(uint32_t value, int bit)
{
    uint32_t v = value & DLS_SINGLE_BIT_MASK;
    return bit == 0 ? v : (v << bit);
}

DLS_EXPORT void *dls_native_create_program(
    const dls_native_node *nodes,
    int32_t node_count,
    const int32_t *output_refs,
    int32_t output_count,
    int32_t scratch_count)
{
    if (node_count < 0 || output_count < 0 || scratch_count < 0) return NULL;

    dls_native_program *program = (dls_native_program *)calloc(1, sizeof(dls_native_program));
    if (!program) return NULL;

    program->node_count = node_count;
    program->output_count = output_count;
    program->scratch_count = scratch_count;

    if (node_count > 0) {
        program->nodes = (dls_native_node *)malloc((size_t)node_count * sizeof(dls_native_node));
        if (!program->nodes) {
            free(program);
            return NULL;
        }
        memcpy(program->nodes, nodes, (size_t)node_count * sizeof(dls_native_node));
    }

    if (output_count > 0) {
        program->output_refs = (int32_t *)malloc((size_t)output_count * sizeof(int32_t));
        if (!program->output_refs) {
            free(program->nodes);
            free(program);
            return NULL;
        }
        memcpy(program->output_refs, output_refs, (size_t)output_count * sizeof(int32_t));
    }

    return program;
}

DLS_EXPORT void dls_native_destroy_program(void *handle)
{
    dls_native_program *program = (dls_native_program *)handle;
    if (!program) return;
    free(program->nodes);
    free(program->output_refs);
    free(program);
}

DLS_EXPORT int32_t dls_native_eval(
    void *handle,
    uint32_t *scratch,
    int32_t scratch_count,
    uint32_t *outputs,
    int32_t output_count)
{
    dls_native_program *program = (dls_native_program *)handle;
    if (!program || !scratch || !outputs) return 0;
    if (scratch_count < program->scratch_count || output_count < program->output_count) return 0;

    for (int32_t i = 0; i < program->node_count; ++i) {
        const dls_native_node *n = &program->nodes[i];
        uint32_t a, b;

        switch (n->op) {
            case DLS_OP_NAND:
                a = load_ref(scratch, n->in_ref[0]);
                b = load_ref(scratch, n->in_ref[1]);
                scratch[n->out_ref[0]] = (1u ^ (a & b)) & 1u;
                break;

            case DLS_OP_TRISTATE:
                scratch[n->out_ref[0]] =
                    (load_ref(scratch, n->in_ref[1]) & 1u)
                        ? load_ref(scratch, n->in_ref[0])
                        : DLS_DISCONNECTED;
                break;

            case DLS_OP_SPLIT4_TO_1:
                a = load_ref(scratch, n->in_ref[0]);
                split_bit(scratch, n->out_ref[0], a, 3);
                split_bit(scratch, n->out_ref[1], a, 2);
                split_bit(scratch, n->out_ref[2], a, 1);
                split_bit(scratch, n->out_ref[3], a, 0);
                break;

            case DLS_OP_SPLIT8_TO_1:
                a = load_ref(scratch, n->in_ref[0]);
                for (int bit = 0; bit < 8; ++bit) {
                    split_bit(scratch, n->out_ref[bit], a, 7 - bit);
                }
                break;

            case DLS_OP_MERGE1_TO_4:
                a =
                    merge_bit(load_ref(scratch, n->in_ref[3]), 0) |
                    merge_bit(load_ref(scratch, n->in_ref[2]), 1) |
                    merge_bit(load_ref(scratch, n->in_ref[1]), 2) |
                    merge_bit(load_ref(scratch, n->in_ref[0]), 3);
                scratch[n->out_ref[0]] = a;
                break;

            case DLS_OP_MERGE1_TO_8:
                a = 0;
                for (int bit = 0; bit < 8; ++bit) {
                    a |= merge_bit(load_ref(scratch, n->in_ref[7 - bit]), bit);
                }
                scratch[n->out_ref[0]] = a;
                break;

            case DLS_OP_MERGE4_TO_8: {
                uint32_t high = load_ref(scratch, n->in_ref[0]);
                uint32_t low = load_ref(scratch, n->in_ref[1]);
                uint32_t bits = ((low & 0xFFFFu) | ((high & 0xFFFFu) << 4)) & 0xFFFFu;
                uint32_t tri = (((low >> 16) & 0xFu) | (((high >> 16) & 0xFu) << 4)) << 16;
                scratch[n->out_ref[0]] = bits | tri;
                break;
            }

            case DLS_OP_SPLIT8_TO_4:
                a = load_ref(scratch, n->in_ref[0]);
                scratch[n->out_ref[0]] = ((a >> 4) & 0xFu) | (((a >> 20) & 0xFu) << 16);
                scratch[n->out_ref[1]] = (a & 0xFu) | (((a >> 16) & 0xFu) << 16);
                break;

            case DLS_OP_BUS:
                scratch[n->out_ref[0]] = load_ref(scratch, n->in_ref[0]);
                break;

            default:
                return 0;
        }
    }

    for (int32_t i = 0; i < program->output_count; ++i) {
        outputs[i] = load_ref(scratch, program->output_refs[i]);
    }

    return 1;
}

DLS_EXPORT uint32_t dls_native_abi_version(void)
{
    return 1u;
}
