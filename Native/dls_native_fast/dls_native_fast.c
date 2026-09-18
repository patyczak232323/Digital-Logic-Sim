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
    int32_t code_words;
    int32_t all_nand;
    int32_t *code;
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

static int32_t node_code_words(uint32_t op)
{
    switch (op) {
        case DLS_OP_NAND:
        case DLS_OP_TRISTATE:
        case DLS_OP_MERGE4_TO_8:
        case DLS_OP_SPLIT8_TO_4:
            return 4;
        case DLS_OP_SPLIT4_TO_1:
        case DLS_OP_MERGE1_TO_4:
            return 6;
        case DLS_OP_SPLIT8_TO_1:
        case DLS_OP_MERGE1_TO_8:
            return 10;
        case DLS_OP_BUS:
            return 3;
        default:
            return 0;
    }
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
    program->all_nand = node_count > 0 ? 1 : 0;

    int32_t words = 0;
    for (int32_t i = 0; i < node_count; ++i) {
        int32_t nwords = node_code_words(nodes[i].op);
        if (nwords == 0) {
            free(program);
            return NULL;
        }
        words += nwords;
        if (nodes[i].op != DLS_OP_NAND) program->all_nand = 0;
    }

    // NAND-only programs get a denser 3-word instruction format with no opcode.
    if (program->all_nand) words = node_count * 3;
    program->code_words = words;

    if (words > 0) {
        program->code = (int32_t *)malloc((size_t)words * sizeof(int32_t));
        if (!program->code) {
            free(program);
            return NULL;
        }

        int32_t *pc = program->code;
        if (program->all_nand) {
            for (int32_t i = 0; i < node_count; ++i) {
                *pc++ = nodes[i].in_ref[0];
                *pc++ = nodes[i].in_ref[1];
                *pc++ = nodes[i].out_ref[0];
            }
        } else {
            for (int32_t i = 0; i < node_count; ++i) {
                const dls_native_node *n = &nodes[i];
                *pc++ = (int32_t)n->op;

                switch (n->op) {
                    case DLS_OP_NAND:
                    case DLS_OP_TRISTATE:
                    case DLS_OP_MERGE4_TO_8:
                        *pc++ = n->in_ref[0];
                        *pc++ = n->in_ref[1];
                        *pc++ = n->out_ref[0];
                        break;

                    case DLS_OP_SPLIT8_TO_4:
                        *pc++ = n->in_ref[0];
                        *pc++ = n->out_ref[0];
                        *pc++ = n->out_ref[1];
                        break;

                    case DLS_OP_SPLIT4_TO_1:
                        *pc++ = n->in_ref[0];
                        for (int j = 0; j < 4; ++j) *pc++ = n->out_ref[j];
                        break;

                    case DLS_OP_MERGE1_TO_4:
                        for (int j = 0; j < 4; ++j) *pc++ = n->in_ref[j];
                        *pc++ = n->out_ref[0];
                        break;

                    case DLS_OP_SPLIT8_TO_1:
                        *pc++ = n->in_ref[0];
                        for (int j = 0; j < 8; ++j) *pc++ = n->out_ref[j];
                        break;

                    case DLS_OP_MERGE1_TO_8:
                        for (int j = 0; j < 8; ++j) *pc++ = n->in_ref[j];
                        *pc++ = n->out_ref[0];
                        break;

                    case DLS_OP_BUS:
                        *pc++ = n->in_ref[0];
                        *pc++ = n->out_ref[0];
                        break;
                }
            }
        }
    }

    if (output_count > 0) {
        program->output_refs = (int32_t *)malloc((size_t)output_count * sizeof(int32_t));
        if (!program->output_refs) {
            free(program->code);
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
    free(program->code);
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

    const int32_t *pc = program->code;

    if (program->all_nand) {
        for (int32_t i = 0; i < program->node_count; ++i) {
            int32_t a_ref = *pc++;
            int32_t b_ref = *pc++;
            int32_t out_ref = *pc++;
            uint32_t a = load_ref(scratch, a_ref);
            uint32_t b = load_ref(scratch, b_ref);
            scratch[out_ref] = (1u ^ (a & b)) & 1u;
        }
    } else {
        for (int32_t i = 0; i < program->node_count; ++i) {
            int32_t op = *pc++;
            uint32_t a;

            switch (op) {
                case DLS_OP_NAND: {
                    int32_t a_ref = *pc++;
                    int32_t b_ref = *pc++;
                    int32_t out_ref = *pc++;
                    scratch[out_ref] = (1u ^ (load_ref(scratch, a_ref) & load_ref(scratch, b_ref))) & 1u;
                    break;
                }

                case DLS_OP_TRISTATE: {
                    int32_t data_ref = *pc++;
                    int32_t enable_ref = *pc++;
                    int32_t out_ref = *pc++;
                    scratch[out_ref] =
                        (load_ref(scratch, enable_ref) & 1u)
                            ? load_ref(scratch, data_ref)
                            : DLS_DISCONNECTED;
                    break;
                }

                case DLS_OP_SPLIT4_TO_1: {
                    int32_t in_ref = *pc++;
                    a = load_ref(scratch, in_ref);
                    split_bit(scratch, *pc++, a, 3);
                    split_bit(scratch, *pc++, a, 2);
                    split_bit(scratch, *pc++, a, 1);
                    split_bit(scratch, *pc++, a, 0);
                    break;
                }

                case DLS_OP_SPLIT8_TO_1: {
                    int32_t in_ref = *pc++;
                    a = load_ref(scratch, in_ref);
                    for (int bit = 0; bit < 8; ++bit) split_bit(scratch, *pc++, a, 7 - bit);
                    break;
                }

                case DLS_OP_MERGE1_TO_4: {
                    int32_t in0 = *pc++;
                    int32_t in1 = *pc++;
                    int32_t in2 = *pc++;
                    int32_t in3 = *pc++;
                    int32_t out_ref = *pc++;
                    scratch[out_ref] =
                        merge_bit(load_ref(scratch, in3), 0) |
                        merge_bit(load_ref(scratch, in2), 1) |
                        merge_bit(load_ref(scratch, in1), 2) |
                        merge_bit(load_ref(scratch, in0), 3);
                    break;
                }

                case DLS_OP_MERGE1_TO_8: {
                    int32_t refs[8];
                    for (int bit = 0; bit < 8; ++bit) refs[bit] = *pc++;
                    int32_t out_ref = *pc++;
                    a = 0;
                    for (int bit = 0; bit < 8; ++bit) {
                        a |= merge_bit(load_ref(scratch, refs[7 - bit]), bit);
                    }
                    scratch[out_ref] = a;
                    break;
                }

                case DLS_OP_MERGE4_TO_8: {
                    int32_t high_ref = *pc++;
                    int32_t low_ref = *pc++;
                    int32_t out_ref = *pc++;
                    uint32_t high = load_ref(scratch, high_ref);
                    uint32_t low = load_ref(scratch, low_ref);
                    uint32_t bits = ((low & 0xFFFFu) | ((high & 0xFFFFu) << 4)) & 0xFFFFu;
                    uint32_t tri = (((low >> 16) & 0xFu) | (((high >> 16) & 0xFu) << 4)) << 16;
                    scratch[out_ref] = bits | tri;
                    break;
                }

                case DLS_OP_SPLIT8_TO_4: {
                    int32_t in_ref = *pc++;
                    int32_t high_out = *pc++;
                    int32_t low_out = *pc++;
                    a = load_ref(scratch, in_ref);
                    scratch[high_out] = ((a >> 4) & 0xFu) | (((a >> 20) & 0xFu) << 16);
                    scratch[low_out] = (a & 0xFu) | (((a >> 16) & 0xFu) << 16);
                    break;
                }

                case DLS_OP_BUS: {
                    int32_t in_ref = *pc++;
                    int32_t out_ref = *pc++;
                    scratch[out_ref] = load_ref(scratch, in_ref);
                    break;
                }

                default:
                    return 0;
            }
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
