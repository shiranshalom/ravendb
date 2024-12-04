import * as yup from "yup";

interface Identity {
    prefix?: string;
    value?: number;
}

export interface DocumentIdentitiesPrefixTestContext {
    isEditing: boolean;
    identities: Identity[];
}

const normalizeString = (value: string) => {
    return value.toLowerCase().trim();
};

export const documentIdentitiesSchema = yup.object({
    prefix: yup
        .string()
        .required()
        .test("not-same-prefix", (value, context) => {
            const { identities, isEditing } = context.options.context as DocumentIdentitiesPrefixTestContext;
            if (isEditing) {
                return true;
            }

            return !identities.some(({ prefix }) => normalizeString(prefix).slice(0, -1) === normalizeString(value));
        }),
    value: yup.number().integer().required(),
});

export type AddIdentitiesFormData = yup.InferType<typeof documentIdentitiesSchema>;
