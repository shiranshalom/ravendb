﻿import React, { useCallback } from "react";
import {
    Button,
    DropdownItem,
    DropdownMenu,
    DropdownToggle,
    UncontrolledDropdown,
    UncontrolledTooltip,
} from "reactstrap";
import genUtils from "common/generalUtils";
import assertUnreachable from "components/utils/assertUnreachable";
import app from "durandal/app";
import showDataDialog from "viewmodels/common/showDataDialog";
import useId from "hooks/useId";
import classNames from "classnames";
import DatabaseLockMode = Raven.Client.ServerWide.DatabaseLockMode;
import {
    RichPanel,
    RichPanelActions,
    RichPanelDetailItem,
    RichPanelDetails,
    RichPanelHeader,
    RichPanelInfo,
    RichPanelName,
    RichPanelStatus,
} from "components/common/RichPanel";
import { useDraggableItem } from "hooks/useDraggableItem";
import { NodeInfo } from "components/models/databases";

interface OrchestratorInfoComponentProps {
    node: NodeInfo;
    deleteFromGroup: (nodeTag: string) => void;
}

export function OrchestratorInfoComponent(props: OrchestratorInfoComponentProps) {
    const { node, deleteFromGroup } = props;

    const lastErrorShort = node.lastError ? genUtils.trimMessage(node.lastError) : null;

    const showErrorsDetails = useCallback(() => {
        app.showBootstrapDialog(new showDataDialog("Error details. Node: " + node.tag, node.lastError, "plain"));
    }, [node]);

    return (
        <RichPanel className="flex-row">
            <RichPanelStatus color={nodeBadgeColor(node)}>{nodeBadgeText(node)}</RichPanelStatus>

            <div className="flex-grow-1">
                <RichPanelHeader>
                    <RichPanelInfo>
                        <RichPanelName title={node.type}>
                            <i className={classNames(cssIcon(node), "me-1")} />
                            Node: {node.tag}
                        </RichPanelName>
                    </RichPanelInfo>
                    <RichPanelActions>
                        <Button type="button" onClick={() => deleteFromGroup(node.tag)}>
                            <i className="icon-disconnected" />
                            Delete from group
                        </Button>
                    </RichPanelActions>
                </RichPanelHeader>
                {lastErrorShort && (
                    <RichPanelDetails>
                        <RichPanelDetailItem
                            label={
                                <>
                                    <i className="icon-warning me-1" />
                                    Error
                                </>
                            }
                        >
                            <a className="link" onClick={showErrorsDetails}>
                                {lastErrorShort}
                            </a>
                        </RichPanelDetailItem>
                    </RichPanelDetails>
                )}
            </div>
        </RichPanel>
    );
}

interface NodeInfoComponentProps {
    node: NodeInfo;
    databaseLockMode: DatabaseLockMode;
    deleteFromGroup: (nodeTag: string, hardDelete: boolean) => void;
}

export function NodeInfoComponent(props: NodeInfoComponentProps) {
    const { node, databaseLockMode, deleteFromGroup } = props;

    const deleteLockId = useId("delete-lock");

    const canDelete = databaseLockMode === "Unlock";
    const lastErrorShort = node.lastError ? genUtils.trimMessage(node.lastError) : null;

    const showErrorsDetails = useCallback(() => {
        app.showBootstrapDialog(new showDataDialog("Error details. Node: " + node.tag, node.lastError, "plain"));
    }, [node]);

    return (
        <RichPanel className="flex-row">
            <RichPanelStatus color={nodeBadgeColor(node)}>{nodeBadgeText(node)}</RichPanelStatus>

            <div className="flex-grow-1">
                <RichPanelHeader>
                    <RichPanelInfo>
                        <RichPanelName title={node.type}>
                            <i className={classNames(cssIcon(node), "me-1")} />
                            Node: {node.tag}
                        </RichPanelName>
                    </RichPanelInfo>
                    <RichPanelActions>
                        {node.responsibleNode && (
                            <div
                                className="text-center"
                                title="Database group node that is responsible for caught up of this node"
                            >
                                <i className="icon-cluster-node"></i>
                                <span>{node.responsibleNode}</span>
                            </div>
                        )}
                        {canDelete ? (
                            <UncontrolledDropdown key="can-delete">
                                <DropdownToggle color="danger" caret>
                                    <i className="icon-disconnected" />
                                    Delete from group
                                </DropdownToggle>
                                <DropdownMenu>
                                    <DropdownItem onClick={() => deleteFromGroup(node.tag, false)}>
                                        <i className="icon-trash" />
                                        <span>Soft Delete</span>&nbsp;
                                        <br />
                                        <small>stop replication and keep database files on the node</small>
                                    </DropdownItem>
                                    <DropdownItem onClick={() => deleteFromGroup(node.tag, true)}>
                                        <i className="icon-alerts text-danger"></i>{" "}
                                        <span className="text-danger">Hard Delete</span>
                                        <br />
                                        &nbsp;<small>stop replication and remove database files on the node</small>
                                    </DropdownItem>
                                </DropdownMenu>
                            </UncontrolledDropdown>
                        ) : (
                            <React.Fragment key="cannot-delete">
                                <UncontrolledDropdown id={deleteLockId}>
                                    <DropdownToggle color="danger" caret disabled>
                                        <i
                                            className={classNames("icon-trash-cutout", {
                                                "icon-addon-exclamation": databaseLockMode === "PreventDeletesError",
                                                "icon-addon-cancel": databaseLockMode === "PreventDeletesIgnore",
                                            })}
                                        />
                                        Delete from group
                                    </DropdownToggle>
                                </UncontrolledDropdown>
                                <UncontrolledTooltip target={deleteLockId} placeholder="top" color="danger">
                                    Database cannot be deleted from node because of the set lock mode
                                </UncontrolledTooltip>
                            </React.Fragment>
                        )}
                    </RichPanelActions>
                </RichPanelHeader>
                {lastErrorShort && (
                    <RichPanelDetails>
                        <RichPanelDetailItem
                            label={
                                <>
                                    <i className="icon-warning me-1" />
                                    Error
                                </>
                            }
                        >
                            <a className="link" onClick={showErrorsDetails}>
                                {lastErrorShort}
                            </a>
                        </RichPanelDetailItem>
                    </RichPanelDetails>
                )}
            </div>
        </RichPanel>
    );
}

interface ShardInfoComponentProps {
    node: NodeInfo;
    databaseLockMode: DatabaseLockMode;
    deleteFromGroup: (nodeTag: string, hardDelete: boolean) => void;
}

export function ShardInfoComponent(props: ShardInfoComponentProps) {
    const { node, databaseLockMode, deleteFromGroup } = props;

    const deleteLockId = useId("delete-lock");

    const canDelete = databaseLockMode === "Unlock";
    const lastErrorShort = node.lastError ? genUtils.trimMessage(node.lastError) : null;

    const showErrorsDetails = useCallback(() => {
        app.showBootstrapDialog(new showDataDialog("Error details. Node: " + node.tag, node.lastError, "plain"));
    }, [node]);

    return (
        <RichPanel className="flex-row">
            <RichPanelStatus color={nodeBadgeColor(node)}>{nodeBadgeText(node)}</RichPanelStatus>

            <div className="flex-grow-1">
                <RichPanelHeader>
                    <RichPanelInfo>
                        <RichPanelName title={node.type}>
                            <i className={classNames(cssIcon(node), "me-1")} />
                            Node: {node.tag}
                        </RichPanelName>
                    </RichPanelInfo>
                    <RichPanelActions>
                        {node.responsibleNode && (
                            <div
                                className="text-center"
                                title="Database group node that is responsible for caught up of this node"
                            >
                                <i className="icon-cluster-node"></i>
                                <span>{node.responsibleNode}</span>
                            </div>
                        )}
                        {canDelete ? (
                            <UncontrolledDropdown key="can-delete">
                                <DropdownToggle color="danger" caret>
                                    <i className="icon-disconnected" />
                                    Delete from group
                                </DropdownToggle>
                                <DropdownMenu>
                                    <DropdownItem onClick={() => deleteFromGroup(node.tag, false)}>
                                        <i className="icon-trash" />
                                        <span>Soft Delete</span>&nbsp;
                                        <br />
                                        <small>stop replication and keep database files on the node</small>
                                    </DropdownItem>
                                    <DropdownItem onClick={() => deleteFromGroup(node.tag, true)}>
                                        <i className="icon-alerts text-danger"></i>{" "}
                                        <span className="text-danger">Hard Delete</span>
                                        <br />
                                        &nbsp;<small>stop replication and remove database files on the node</small>
                                    </DropdownItem>
                                </DropdownMenu>
                            </UncontrolledDropdown>
                        ) : (
                            <React.Fragment key="cannot-delete">
                                <UncontrolledDropdown id={deleteLockId}>
                                    <DropdownToggle color="danger" caret disabled>
                                        <i
                                            className={classNames("icon-trash-cutout", {
                                                "icon-addon-exclamation": databaseLockMode === "PreventDeletesError",
                                                "icon-addon-cancel": databaseLockMode === "PreventDeletesIgnore",
                                            })}
                                        />
                                        Delete from group
                                    </DropdownToggle>
                                </UncontrolledDropdown>
                                <UncontrolledTooltip target={deleteLockId} placeholder="top" color="danger">
                                    Database cannot be deleted from node because of the set lock mode
                                </UncontrolledTooltip>
                            </React.Fragment>
                        )}
                    </RichPanelActions>
                </RichPanelHeader>
                {lastErrorShort && (
                    <RichPanelDetails>
                        <RichPanelDetailItem
                            label={
                                <>
                                    <i className="icon-warning me-1" />
                                    Error
                                </>
                            }
                        >
                            <a className="link" onClick={showErrorsDetails}>
                                {lastErrorShort}
                            </a>
                        </RichPanelDetailItem>
                    </RichPanelDetails>
                )}
            </div>
        </RichPanel>
    );
}

interface NodeInfoReorderComponentProps {
    node: NodeInfo;
    findCardIndex: (node: NodeInfo) => number;
    setOrder: (action: (state: NodeInfo[]) => NodeInfo[]) => void;
}

const tagExtractor = (node: NodeInfo) => node.tag;

export function NodeInfoReorderComponent(props: NodeInfoReorderComponentProps) {
    const { node, setOrder, findCardIndex } = props;

    const { drag, drop, isDragging } = useDraggableItem("node", node, tagExtractor, findCardIndex, setOrder);

    const opacity = isDragging ? 0.5 : 1;

    return (
        <div ref={(node) => drag(drop(node))} style={{ opacity }}>
            <RichPanel className="flex-row">
                <RichPanelStatus color={nodeBadgeColor(node)}>{nodeBadgeText(node)}</RichPanelStatus>

                <div className="flex-grow-1">
                    <RichPanelHeader>
                        <RichPanelInfo>
                            <RichPanelName title={node.type}>
                                <i className={classNames(cssIcon(node), "me-1")} />
                                Node: {node.tag}
                            </RichPanelName>
                        </RichPanelInfo>

                        <RichPanelActions>
                            {node.responsibleNode && (
                                <div
                                    className="text-center"
                                    title="Database group node that is responsible for caught up of this node"
                                >
                                    <i className="icon-cluster-node"></i>
                                    <span>{node.responsibleNode}</span>
                                </div>
                            )}
                        </RichPanelActions>
                    </RichPanelHeader>
                </div>
            </RichPanel>
        </div>
    );
}

function nodeBadgeColor(node: NodeInfo) {
    switch (node.lastStatus) {
        case "Ok":
            return "success";
        case "NotResponding":
            return "danger";
        default:
            return "warning";
    }
}

function cssIcon(node: NodeInfo) {
    const type = node.type;

    switch (type) {
        case "Member":
            return "icon-dbgroup-member";
        case "Promotable":
            return "icon-dbgroup-promotable";
        case "Rehab":
            return "icon-dbgroup-rehab";
        default:
            assertUnreachable(type);
    }
}

function nodeBadgeText(node: NodeInfo) {
    switch (node.lastStatus) {
        case "Ok":
            return "Active";
        case "NotResponding":
            return "Error";
        default:
            return "Catching up";
    }
}
