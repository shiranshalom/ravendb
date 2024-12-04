import { CellContext, ColumnDef } from "@tanstack/react-table";
import { CellValueWrapper } from "components/common/virtualTable/cells/CellValue";
import { virtualTableUtils } from "components/common/virtualTable/utils/virtualTableUtils";
import { Button } from "reactstrap";
import { Icon } from "components/common/Icon";
import DocumentIdentitiesModal from "components/pages/database/documents/identities/DocumentIdentitiesModal";
import { useAppSelector } from "components/store";
import { accessManagerSelectors } from "components/common/shell/accessManagerSliceSelectors";
import useBoolean from "hooks/useBoolean";
import { AddIdentitiesFormData } from "components/pages/database/documents/identities/DocumentIdentitiesValidation";

export function useDocumentIdentitiesColumns(availableWidth: number, reload: () => void) {
    const databaseAccessWrite = useAppSelector(accessManagerSelectors.getHasDatabaseWriteAccess)();
    const bodyWidth = virtualTableUtils.getTableBodyWidth(availableWidth);
    const getSize = virtualTableUtils.getCellSizeProvider(bodyWidth);

    const identitiesColumns: ColumnDef<AddIdentitiesFormData>[] = [
        {
            header: "Document ID Prefix",
            accessorKey: "prefix",
            cell: CellValueWrapper,
            size: getSize(!databaseAccessWrite ? 50 : 46),
        },
        {
            header: "Latest value",
            accessorKey: "value",
            cell: CellValueWrapper,
            size: getSize(!databaseAccessWrite ? 50 : 46),
        },
    ];

    if (databaseAccessWrite) {
        identitiesColumns.push({
            id: "actions",
            header: "Edit",
            cell: (props) => <CellValueButtonWrapper refetch={reload} {...props} />,
            size: getSize(8),
        });
    }

    return {
        identitiesColumns,
    };
}

type CellValueButtonWrapperProps = CellContext<AddIdentitiesFormData, unknown> & { refetch: () => void };

function CellValueButtonWrapper(args: CellValueButtonWrapperProps) {
    const { value: isOpen, toggle: setIsOpen } = useBoolean(false);

    return (
        <>
            <Button onClick={setIsOpen}>
                <Icon icon="edit" margin="me-0" />
            </Button>
            <DocumentIdentitiesModal
                refetch={args.refetch}
                isOpen={isOpen}
                defaultValues={args.row.original}
                toggleModal={setIsOpen}
            />
        </>
    );
}
