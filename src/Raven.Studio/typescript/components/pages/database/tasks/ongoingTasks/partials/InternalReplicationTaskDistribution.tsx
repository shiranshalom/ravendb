import {
    OngoingInternalReplicationNodeInfo,
    OngoingReplicationProgressAwareTaskNodeInfo,
    OngoingTaskAbstractReplicationNodeInfoDetails,
    OngoingTaskNodeInternalReplicationProgressDetails,
} from "components/models/tasks";
import { DistributionItem, DistributionLegend, LocationDistribution } from "components/common/LocationDistribution";
import { Icon } from "components/common/Icon";
import React, { useState } from "react";
import classNames from "classnames";
import { ProgressCircle } from "components/common/ProgressCircle";
import { ReplicationTaskProgressTooltip } from "components/pages/database/tasks/ongoingTasks/partials/ReplicationTaskProgressTooltip";
import { withPreventDefault } from "components/utils/common";
import { ErrorModal } from "components/pages/database/tasks/ongoingTasks/partials/ErrorModal";

interface ItemWithTooltipProps {
    nodeInfo: OngoingInternalReplicationNodeInfo;
    sharded: boolean;
    progress: OngoingTaskNodeInternalReplicationProgressDetails;
}

function ItemWithTooltip(props: ItemWithTooltipProps) {
    const { nodeInfo, sharded, progress } = props;

    const shard = (
        <div className="top shard">
            {nodeInfo.location.shardNumber != null && (
                <>
                    <Icon icon="shard" />
                    {nodeInfo.location.shardNumber}
                </>
            )}
        </div>
    );

    const [errorToDisplay, setErrorToDisplay] = useState<string>(null);

    const toggleErrorModal = () => {
        setErrorToDisplay((error) => (error ? null : nodeInfo.error));
    };

    const key = taskNodeInfoKey(nodeInfo);
    const [node, setNode] = useState<HTMLDivElement>();

    const firstProgress = nodeInfo.progress[0];
    const hasError = nodeInfo.status === "failure";
    return (
        <div ref={setNode}>
            <DistributionItem loading={nodeInfo.status === "loading" || nodeInfo.status === "idle"} key={key}>
                {sharded && shard}
                <div className={classNames("node", { top: !sharded })}>
                    {!sharded && <Icon icon="node" />}
                    {nodeInfo.location.nodeTag} &gt; {progress?.destinationNodeTag ?? "?"}
                </div>
                <div>{firstProgress?.lastDatabaseEtag ? firstProgress.lastDatabaseEtag.toLocaleString() : "-"}</div>
                <div>{firstProgress?.lastSentEtag ? firstProgress.lastSentEtag.toLocaleString() : "-"}</div>
                <div>
                    {hasError ? (
                        <a href="#" onClick={withPreventDefault(toggleErrorModal)}>
                            <Icon icon="warning" color="danger" margin="m-0" />
                        </a>
                    ) : (
                        "-"
                    )}
                </div>
                <InternalReplicationTaskProgress nodeInfo={nodeInfo} />
            </DistributionItem>
            {node &&
                (errorToDisplay ? (
                    <ErrorModal key="modal" toggleErrorModal={toggleErrorModal} error={errorToDisplay} />
                ) : (
                    <ReplicationTaskProgressTooltip
                        hasError={nodeInfo.status === "failure"}
                        toggleErrorModal={toggleErrorModal}
                        target={node}
                        progress={nodeInfo.progress}
                        status={nodeInfo.status}
                        lastAcceptedChangeVectorFromDestination={
                            nodeInfo.progress[0]?.lastAcceptedChangeVectorFromDestination
                        }
                        sourceDatabaseChangeVector={nodeInfo.progress[0]?.sourceDatabaseChangeVector}
                    />
                ))}
        </div>
    );
}

interface InternalReplicationTaskDistributionProps {
    data: OngoingInternalReplicationNodeInfo[];
}

export function InternalReplicationTaskDistribution(props: InternalReplicationTaskDistributionProps) {
    const { data } = props;

    const sharded = data.some((x) => x.location.shardNumber != null);

    const items = data.flatMap((nodeInfo) => {
        if (!nodeInfo.progress.length) {
            const key = taskNodeInfoKey(nodeInfo) + "->" + "?";
            return <ItemWithTooltip key={key} nodeInfo={nodeInfo} sharded={sharded} progress={null} />;
        }

        return nodeInfo.progress.map((progress) => {
            const key = taskNodeInfoKey(nodeInfo) + "->" + progress.destinationNodeTag;
            return <ItemWithTooltip key={key} nodeInfo={nodeInfo} progress={progress} sharded={sharded} />;
        });
    });

    return (
        <div className="px-3 pb-2">
            <LocationDistribution>
                <DistributionLegend>
                    <div className="top"></div>
                    {sharded && (
                        <div className="node">
                            <Icon icon="node" /> Node
                        </div>
                    )}
                    <div>
                        <Icon icon="etag" /> Last DB Etag
                    </div>
                    <div>
                        <Icon icon="etag" /> Last Sent Etag
                    </div>
                    <div>
                        <Icon icon="warning" /> Error
                    </div>
                    <div>
                        <Icon icon="changes" /> State
                    </div>
                </DistributionLegend>
                {items}
            </LocationDistribution>
        </div>
    );
}

interface InternalReplicationTaskProgressProps {
    nodeInfo: OngoingReplicationProgressAwareTaskNodeInfo<OngoingTaskAbstractReplicationNodeInfoDetails>;
}

export function InternalReplicationTaskProgress(props: InternalReplicationTaskProgressProps) {
    const { nodeInfo } = props;

    if (!nodeInfo.progress) {
        return <ProgressCircle state="running" />;
    }

    if (nodeInfo.progress.every((x) => x.completed)) {
        return (
            <ProgressCircle state="success" icon="check">
                up to date
            </ProgressCircle>
        );
    }

    // at least one transformation is not completed - let's calculate total progress
    const totalItems = nodeInfo.progress.reduce((acc, current) => acc + current.global.total, 0);
    const totalProcessed = nodeInfo.progress.reduce((acc, current) => acc + current.global.processed, 0);

    const percentage = Math.floor((totalProcessed * 100) / totalItems) / 100;

    return (
        <ProgressCircle state="running" icon={null} progress={percentage}>
            Running
        </ProgressCircle>
    );
}

const taskNodeInfoKey = (nodeInfo: OngoingInternalReplicationNodeInfo) => {
    return nodeInfo.location.shardNumber + "__" + nodeInfo.location.nodeTag;
};
