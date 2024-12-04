import { ModalProps } from "reactstrap/types/lib/Modal";
import { Button, FormGroup, InputGroup, Label, Modal, ModalBody, ModalFooter } from "reactstrap";
import React from "react";
import { Icon } from "components/common/Icon";
import { FormInput } from "components/common/Form";
import { Control, SubmitHandler, useForm, useWatch } from "react-hook-form";
import {
    AddIdentitiesFormData,
    DocumentIdentitiesPrefixTestContext,
    documentIdentitiesSchema,
} from "components/pages/database/documents/identities/DocumentIdentitiesValidation";
import ButtonWithSpinner from "components/common/ButtonWithSpinner";
import { useServices } from "hooks/useServices";
import { useAppSelector } from "components/store";
import { databaseSelectors } from "components/common/shell/databaseSliceSelectors";
import { tryHandleSubmit } from "components/utils/common";
import RichAlert from "components/common/RichAlert";
import { useAsync } from "react-async-hook";
import { LazyLoad } from "components/common/LazyLoad";
import { yupResolver } from "@hookform/resolvers/yup";

interface DocumentIdentitiesModalProps extends ModalProps {
    toggleModal: () => void;
    defaultValues?: AddIdentitiesFormData;
    identities?: AddIdentitiesFormData[];
    refetch: () => void;
}

export default function DocumentIdentitiesModal({
    defaultValues,
    refetch,
    identities,
    ...props
}: DocumentIdentitiesModalProps) {
    const databaseName = useAppSelector(databaseSelectors.activeDatabaseName);
    const { databasesService } = useServices();
    const isEditing = !!defaultValues;
    const form = useForm<AddIdentitiesFormData, DocumentIdentitiesPrefixTestContext>({
        context: {
            identities,
            isEditing,
        },
        resolver: yupResolver(documentIdentitiesSchema),
        defaultValues,
    });

    const formValues = useWatch({ control: form.control });

    const onSubmit: SubmitHandler<AddIdentitiesFormData> = ({ prefix, value }) => {
        return tryHandleSubmit(async () => {
            await databasesService.seedIdentity(databaseName, prefix, value);
            form.reset();
            refetch();
            props.toggleModal();
        });
    };

    return (
        <Modal contentClassName="modal-border bulge-primary" wrapClassName="bs5" size="lg" {...props}>
            <form onSubmit={form.handleSubmit(onSubmit)}>
                <ModalBody>
                    <div className="position-absolute m-2 end-0 top-0">
                        <Button close onClick={props.toggleModal} />
                    </div>
                    <div className="w-100 d-flex align-items-center justify-content-center flex-column">
                        <Icon size="xl" icon="identities" color="primary" margin="me-0" />
                        <h4>{isEditing ? "Edit Identity" : "Add new identity"}</h4>
                    </div>
                    <div className="w-100 d-flex flex-column gap-4 mb-4">
                        <DocumentIdentitiesModalForm isEditing={isEditing} control={form.control} />
                    </div>
                    <InformationBadge {...formValues} />
                </ModalBody>
                <ModalFooter>
                    <Button className="link-muted" color="link" onClick={props.toggleModal} type="button">
                        Close
                    </Button>
                    <ButtonWithSpinner
                        className="rounded-pill"
                        color="success"
                        icon="save"
                        isSpinning={form.formState.isSubmitting}
                        type="submit"
                    >
                        Save identity
                    </ButtonWithSpinner>
                </ModalFooter>
            </form>
        </Modal>
    );
}

function InformationBadge({ prefix = "<Prefix>", value }: AddIdentitiesFormData) {
    const databaseName = useAppSelector(databaseSelectors.activeDatabaseName);
    const { manageServerService } = useServices();
    const { result, loading } = useAsync(async () => {
        const clientConfiguration = manageServerService.getClientConfiguration(databaseName);
        const globalClientConfiguration = manageServerService.getGlobalClientConfiguration();

        const [clientConfig, globalConfig] = await Promise.allSettled([clientConfiguration, globalClientConfiguration]);

        if (
            clientConfig.status === "fulfilled" &&
            !clientConfig.value?.Disabled &&
            clientConfig.value?.IdentityPartsSeparator != null
        ) {
            return clientConfig.value.IdentityPartsSeparator;
        }

        if (
            globalConfig.status === "fulfilled" &&
            !globalConfig.value?.Disabled &&
            globalConfig.value?.IdentityPartsSeparator != null
        ) {
            return globalConfig.value.IdentityPartsSeparator;
        }

        return "/";
    }, []);

    return (
        <RichAlert variant="info">
            <div className="word-break">
                <p className="mb-0">
                    The effective identity separator in configuration is:{" "}
                    <strong>
                        <LazyLoad active={loading}>{result}</LazyLoad>
                    </strong>
                </p>
                <p className="mb-0">
                    The next document that will be created with prefix &quot;<strong>{prefix}|</strong>&quot; will have
                    ID: &quot;
                    <strong>
                        <code>
                            {prefix}
                            {result}
                            {value ?? `<Value + 1>`}
                        </code>
                    </strong>
                    &quot;
                </p>
            </div>
        </RichAlert>
    );
}

interface DocumentIdentitiesModalFormProps {
    control: Control<AddIdentitiesFormData>;
    isEditing?: boolean;
}

function DocumentIdentitiesModalForm({ control, isEditing }: DocumentIdentitiesModalFormProps) {
    return (
        <FormGroup>
            <InputGroup className="vstack my-1">
                <Label>Prefix</Label>
                <FormInput
                    name="prefix"
                    type="text"
                    control={control}
                    placeholder="Enter the document id prefix"
                    disabled={isEditing}
                />
            </InputGroup>
            <InputGroup className="vstack my-1">
                <Label>Value</Label>
                <FormInput name="value" type="number" control={control} placeholder="Enter identity value" />
            </InputGroup>
        </FormGroup>
    );
}
