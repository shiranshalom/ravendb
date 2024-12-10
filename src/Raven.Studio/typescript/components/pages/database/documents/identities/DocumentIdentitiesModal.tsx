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
import { useEventsCollector } from "hooks/useEventsCollector";

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
    toggleModal,
    ...props
}: DocumentIdentitiesModalProps) {
    const databaseName = useAppSelector(databaseSelectors.activeDatabaseName);
    const { databasesService } = useServices();
    const isEditing = !!defaultValues;
    const eventsCollector = useEventsCollector();
    const {
        reset,
        handleSubmit,
        control,
        formState: { isSubmitting },
    } = useForm<AddIdentitiesFormData, DocumentIdentitiesPrefixTestContext>({
        context: {
            identities,
            isEditing,
        },
        resolver: yupResolver(documentIdentitiesSchema),
        defaultValues,
    });

    const formValues = useWatch({ control });

    const onSubmit: SubmitHandler<AddIdentitiesFormData> = ({ prefix, value }) => {
        return tryHandleSubmit(async () => {
            await databasesService.seedIdentity(databaseName, prefix, value);
            if (!isEditing) {
                eventsCollector.reportEvent("identity", "new");
            }
            refetch();
            toggleModal();
            reset();
        });
    };

    return (
        <Modal centered contentClassName="modal-border bulge-primary" wrapClassName="bs5" size="lg" {...props}>
            <form onSubmit={handleSubmit(onSubmit)}>
                <ModalBody>
                    <div className="position-absolute m-2 end-0 top-0">
                        <Button close onClick={toggleModal} />
                    </div>
                    <div className="w-100 d-flex align-items-center justify-content-center flex-column">
                        <Icon size="xl" icon="identities" color="primary" margin="me-0" />
                        <h4>{isEditing ? "Edit Identity" : "Add new identity"}</h4>
                    </div>
                    <div className="w-100 d-flex flex-column gap-4 mb-4">
                        <DocumentIdentitiesModalForm isEditing={isEditing} control={control} />
                    </div>
                    <InformationBadge isEditing={isEditing} {...formValues} />
                </ModalBody>
                <ModalFooter>
                    <Button className="link-muted" color="link" onClick={toggleModal} type="button">
                        Close
                    </Button>
                    <ButtonWithSpinner
                        className="rounded-pill"
                        color="success"
                        icon="save"
                        isSpinning={isSubmitting}
                        type="submit"
                    >
                        Save identity
                    </ButtonWithSpinner>
                </ModalFooter>
            </form>
        </Modal>
    );
}

interface InformationBadgeProps extends AddIdentitiesFormData {
    isEditing: boolean;
}

function InformationBadge({ prefix = "<Prefix>", value, isEditing }: InformationBadgeProps) {
    const databaseName = useAppSelector(databaseSelectors.activeDatabaseName);
    const { manageServerService } = useServices();
    const { result: identityPartsSeparator, loading } = useAsync(async () => {
        try {
            const clientConfiguration = manageServerService.getClientConfiguration(databaseName);
            const globalClientConfiguration = manageServerService.getGlobalClientConfiguration();

            const [clientConfig, globalConfig] = await Promise.all([clientConfiguration, globalClientConfiguration]);

            if (!clientConfig?.Disabled && clientConfig?.IdentityPartsSeparator != null) {
                return clientConfig.IdentityPartsSeparator;
            }

            if (!globalConfig?.Disabled && globalConfig?.IdentityPartsSeparator != null) {
                return globalConfig.IdentityPartsSeparator;
            }

            return "/";
        } catch (error) {
            console.error("Error fetching configurations:", error);
            return "/";
        }
    }, []);

    const formattedPrefix = isEditing ? prefix.slice(0, -1) : prefix;

    return (
        <RichAlert variant="info">
            <div className="word-break">
                <p className="mb-0">
                    The effective identity separator in configuration is:{" "}
                    <strong>
                        <LazyLoad active={loading}>{identityPartsSeparator}</LazyLoad>
                    </strong>
                </p>
                <p className="mb-0">
                    The next document that will be created with prefix &quot;
                    <strong>{formattedPrefix}|</strong>
                    &quot; will have ID: &quot;
                    <strong>
                        <code>
                            {formattedPrefix}
                            {identityPartsSeparator}
                            {value ? value + 1 : `<Value + 1>`}
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
