﻿import React from "react";
import { OngoingTasksPage } from "../OngoingTasksPage";
import { Meta, StoryObj } from "@storybook/react";
import { withStorybookContexts, withBootstrap5, withForceRerender } from "test/storybookTestUtils";
import clusterTopologyManager from "common/shell/clusterTopologyManager";
import { mockServices } from "test/mocks/services/MockServices";
import { TasksStubs } from "test/stubs/TasksStubs";
import { boundCopy } from "components/utils/common";
import OngoingTaskRavenEtlListView = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskRavenEtl;
import OngoingTaskSqlEtlListView = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskSqlEtl;
import OngoingTaskOlapEtlListView = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskOlapEtl;
import OngoingTaskElasticSearchEtlListView = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskElasticSearchEtl;
import OngoingTaskQueueEtlListView = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskQueueEtl;
import OngoingTaskQueueSinkListView = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskQueueSink;
import OngoingTaskPullReplicationAsSink = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskPullReplicationAsSink;
import OngoingTaskPullReplicationAsHub = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskPullReplicationAsHub;
import OngoingTaskBackup = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskBackup;
import OngoingTaskReplication = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskReplication;
import OngoingTaskSubscription = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskSubscription;
import { mockStore } from "test/mocks/store/MockStore";
import { userEvent, within } from "@storybook/test";
import { MockedValue } from "test/mocks/services/AutoMockService";
import OngoingTasksResult = Raven.Server.Web.System.OngoingTasksResult;
import MockTasksService from "test/mocks/services/MockTasksService";
import assertUnreachable from "components/utils/assertUnreachable";

export default {
    title: "Pages/Tasks/Ongoing Tasks",
    decorators: [withStorybookContexts, withBootstrap5, withForceRerender],
    excludeStories: /Template$/, //TODO:
    parameters: {
        design: {
            type: "figma",
            url: "https://www.figma.com/design/Dym4dtdwQ0j4mg9nmC4ZDI/Pages---Ongoing-Tasks?node-id=0-1&t=p61ns0HrN2R1ZUAu-1",
        },
    },
} satisfies Meta;

//TODO: add stories for reconnected/errored state?

export function commonInit(databaseType: "sharded" | "cluster" | "singleNode" = "sharded") {
    const { accessManager, license, databases } = mockStore;
    const { tasksService, licenseService } = mockServices;

    switch (databaseType) {
        case "sharded":
            databases.withActiveDatabase_Sharded();
            break;
        case "cluster":
            databases.withActiveDatabase_NonSharded_Cluster();
            break;
        case "singleNode":
            databases.withActiveDatabase_NonSharded_SingleNode();
            break;
        default:
            assertUnreachable(databaseType);
    }

    accessManager.with_securityClearance("ClusterAdmin");

    license.with_License();

    clusterTopologyManager.default.localNodeTag = ko.pureComputed(() => "A");

    licenseService.withLimitsUsage();
    tasksService.withGetSubscriptionTaskInfo();
    tasksService.withGetSubscriptionConnectionDetails();
    tasksService.withGetExternalReplicationProgress((dto) => {
        dto.Results = [];
    });
    tasksService.withGetEtlProgress((dto) => {
        dto.Results = [];
    });
    tasksService.withGetInternalReplicationProgress((dto) => {
        dto.Results = [];
    });
}

export const EmptyView: StoryObj = {
    render: () => {
        commonInit();

        const { databases } = mockStore;
        databases.withActiveDatabase_NonSharded_SingleNode();

        const { tasksService } = mockServices;

        tasksService.withGetTasks((dto) => {
            dto.SubscriptionsCount = 0;
            dto.OngoingTasks = [];
            dto.PullReplications = [];
        });
        tasksService.withGetEtlProgress((dto) => {
            dto.Results = [];
        });
        tasksService.withGetInternalReplicationProgress((dto) => {
            dto.Results = [];
        });

        return <OngoingTasksPage />;
    },
};

export const FullView: StoryObj = {
    render: () => {
        commonInit();

        const { tasksService } = mockServices;

        tasksService.withGetTasks();
        tasksService.withGetEtlProgress();
        tasksService.withGetExternalReplicationProgress();
        tasksService.withGetInternalReplicationProgress();

        return <OngoingTasksPage />;
    },
};

export const InternalReplication: StoryObj = {
    render: () => {
        commonInit();

        const { tasksService } = mockServices;

        tasksService.withGetTasks((x) => {
            x.OngoingTasks = [];
            x.PullReplications = [];
            x.SubscriptionsCount = 0;
        });

        tasksService.withGetInternalReplicationProgress();

        return <OngoingTasksPage />;
    },
};

export const SubscriptionTemplate = (args: {
    disabled?: boolean;
    customizeTask?: (x: OngoingTaskSubscription) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const ongoingTask = TasksStubs.getSubscription();
        if (args.disabled) {
            ongoingTask.TaskState = "Disabled";
        }
        args.customizeTask?.(ongoingTask);
        x.OngoingTasks = [ongoingTask];
        x.PullReplications = [];
        x.SubscriptionsCount = 1;
    });

    return <OngoingTasksPage />;
};

export const SubscriptionsWithLicenseLimits = () => {
    commonInit();

    const { license } = mockStore;
    license.with_LicenseLimited();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        x.OngoingTasks = [
            TasksStubs.getSubscription(),
            {
                ...TasksStubs.getSubscription(),
                TaskName: "SomeSecondSub",
            },
        ];
        x.PullReplications = [];
        x.SubscriptionsCount = 2;
    });

    return <OngoingTasksPage />;
};

export const SubscriptionDisabled = boundCopy(SubscriptionTemplate, {
    disabled: true,
});

export const SubscriptionEnabled = boundCopy(SubscriptionTemplate, {
    disabled: false,
});

export const ShardedSubscription = boundCopy(SubscriptionTemplate, {
    customizeTask: (x) => {
        x.ChangeVectorForNextBatchStartingPoint = null;
        x.ChangeVectorForNextBatchStartingPointPerShard = {
            "1": "B:884-7YtyJhmi/k+as1eW7RRJWQ, A:856-TtyicrkQAUKtvYiwGx0yoA",
            "0": "B:884-7YtyJhmi/k+as1eW7RRJWQ, A:856-TtyicrkQAUKtvYiwGx0yoA",
            "2": "B:884-7YtyJhmi/k+as1eW7RRJWQ, A:856-TtyicrkQAUKtvYiwGx0yoA",
        };
    },
});

interface RavenEtlProps {
    disabled?: boolean;
    completed?: boolean;
    emptyScript?: boolean;
    customizeTask?: (x: OngoingTaskRavenEtlListView) => void;
}

export const RavenEtl: StoryObj<RavenEtlProps> = {
    render: (args: RavenEtlProps) => {
        commonInit();

        const { tasksService } = mockServices;

        tasksService.withGetTasks((x) => {
            const ravenEtl = TasksStubs.getRavenEtl();
            if (args.disabled) {
                ravenEtl.TaskState = "Disabled";
            }
            args.customizeTask?.(ravenEtl);
            x.OngoingTasks = [ravenEtl];
            x.PullReplications = [];
            x.SubscriptionsCount = 0;
        });

        mockEtlProgress(tasksService, args.completed, args.disabled, args.emptyScript);

        return <OngoingTasksPage />;
    },
    args: {
        completed: false,
        disabled: false,
        emptyScript: false,
        customizeTask: undefined,
    },
    play: async ({ canvas }) => {
        const container = within(await canvas.findByTestId("raven-etls"));
        await userEvent.click(await container.findByTitle(/Click for details/));
    },
};

export const RavenEtlDisabled = {
    ...RavenEtl,
    args: {
        ...RavenEtl.args,
        disabled: true,
    },
};

export const RavenEtlCompleted = {
    ...RavenEtl,
    args: {
        ...RavenEtl.args,
        completed: true,
        disabled: false,
    },
};

export const RavenEtlEmptyScript = {
    ...RavenEtl,
    args: {
        ...RavenEtl.args,
        completed: true,
        emptyScript: true,
    },
};

export const SqlTemplate = (args: {
    disabled?: boolean;
    completed?: boolean;
    emptyScript?: boolean;
    customizeTask?: (x: OngoingTaskSqlEtlListView) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const sqlEtl = TasksStubs.getSql();
        if (args.disabled) {
            sqlEtl.TaskState = "Disabled";
        }
        args.customizeTask?.(sqlEtl);
        x.OngoingTasks = [sqlEtl];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    mockEtlProgress(tasksService, args.completed, args.disabled, args.emptyScript);

    return <OngoingTasksPage />;
};

export const SqlDisabled = boundCopy(SqlTemplate, {
    disabled: true,
});

export const SqlCompleted = boundCopy(SqlTemplate, {
    completed: true,
});

export const SqlEmptyScript = boundCopy(SqlTemplate, {
    completed: true,
    emptyScript: true,
});

export const OlapTemplate = (args: {
    disabled?: boolean;
    completed?: boolean;
    emptyScript?: boolean;
    customizeTask?: (x: OngoingTaskOlapEtlListView) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const etl = TasksStubs.getOlap();
        if (args.disabled) {
            etl.TaskState = "Disabled";
        }
        args.customizeTask?.(etl);
        x.OngoingTasks = [etl];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    mockEtlProgress(tasksService, args.completed, args.disabled, args.emptyScript);

    return <OngoingTasksPage />;
};

export const OlapDisabled = boundCopy(OlapTemplate, {
    disabled: true,
});

export const OlapCompleted = boundCopy(OlapTemplate, {
    completed: true,
});

export const OlapEmptyScript = boundCopy(OlapTemplate, {
    completed: true,
    emptyScript: true,
});

export const ElasticSearchTemplate = (args: {
    disabled?: boolean;
    completed?: boolean;
    emptyScript?: boolean;
    customizeTask?: (x: OngoingTaskElasticSearchEtlListView) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const etl = TasksStubs.getElasticSearch();
        if (args.disabled) {
            etl.TaskState = "Disabled";
        }
        args.customizeTask?.(etl);
        x.OngoingTasks = [etl];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    mockEtlProgress(tasksService, args.completed, args.disabled, args.emptyScript);

    return <OngoingTasksPage />;
};

export const ElasticSearchDisabled = boundCopy(ElasticSearchTemplate, {
    disabled: true,
});

export const ElasticSearchCompleted = boundCopy(ElasticSearchTemplate, {
    completed: true,
});

export const ElasticSearchEmptyScript = boundCopy(ElasticSearchTemplate, {
    completed: true,
    emptyScript: true,
});

export const KafkaEtlTemplate = (args: {
    disabled?: boolean;
    completed?: boolean;
    emptyScript?: boolean;
    customizeTask?: (x: OngoingTaskQueueEtlListView) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const etl = TasksStubs.getKafkaEtl();
        if (args.disabled) {
            etl.TaskState = "Disabled";
        }
        args.customizeTask?.(etl);
        x.OngoingTasks = [etl];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    mockEtlProgress(tasksService, args.completed, args.disabled, args.emptyScript);

    return <OngoingTasksPage />;
};

export const KafkaEtlDisabled = boundCopy(KafkaEtlTemplate, {
    disabled: true,
});

export const KafkaEtlCompleted = boundCopy(KafkaEtlTemplate, {
    completed: true,
});

export const KafkaEtlEmptyScript = boundCopy(KafkaEtlTemplate, {
    completed: true,
    emptyScript: true,
});

export const RabbitEtlTemplate = (args: {
    disabled?: boolean;
    completed?: boolean;
    emptyScript?: boolean;
    customizeTask?: (x: OngoingTaskQueueEtlListView) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const etl = TasksStubs.getRabbitEtl();
        if (args.disabled) {
            etl.TaskState = "Disabled";
        }
        args.customizeTask?.(etl);
        x.OngoingTasks = [etl];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    mockEtlProgress(tasksService, args.completed, args.disabled, args.emptyScript);

    return <OngoingTasksPage />;
};

export const RabbitEtlDisabled = boundCopy(RabbitEtlTemplate, {
    disabled: true,
});

export const RabbitEtlCompleted = boundCopy(RabbitEtlTemplate, {
    completed: true,
});

export const RabbitEtlEmptyScript = boundCopy(RabbitEtlTemplate, {
    completed: true,
    emptyScript: true,
});

export const AzureQueueStorageEtlTemplate = (args: {
    disabled?: boolean;
    completed?: boolean;
    emptyScript?: boolean;
    customizeTask?: (x: OngoingTaskQueueEtlListView) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const etl = TasksStubs.getAzureQueueStorageEtl();
        if (args.disabled) {
            etl.TaskState = "Disabled";
        }
        args.customizeTask?.(etl);
        x.OngoingTasks = [etl];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    mockEtlProgress(tasksService, args.completed, args.disabled, args.emptyScript);

    return <OngoingTasksPage />;
};

export const AzureQueueStorageEtlDisabled = boundCopy(AzureQueueStorageEtlTemplate, {
    disabled: true,
});

export const AzureQueueStorageEtlCompleted = boundCopy(AzureQueueStorageEtlTemplate, {
    completed: true,
});

export const AzureQueueStorageEtlEmptyScript = boundCopy(AzureQueueStorageEtlTemplate, {
    completed: true,
    emptyScript: true,
});

export const KafkaSinkTemplate = (args: {
    disabled?: boolean;
    customizeTask?: (x: OngoingTaskQueueSinkListView) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const sink = TasksStubs.getKafkaSink();
        if (args.disabled) {
            sink.TaskState = "Disabled";
        }
        x.OngoingTasks = [sink];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    return <OngoingTasksPage />;
};

export const KafkaSinkDisabled = boundCopy(KafkaSinkTemplate, {
    disabled: true,
});

export const KafkaSinkCompleted = boundCopy(KafkaSinkTemplate, {});

export const RabbitSinkTemplate = (args: {
    disabled?: boolean;
    customizeTask?: (x: OngoingTaskQueueSinkListView) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const sink = TasksStubs.getRabbitSink();
        if (args.disabled) {
            sink.TaskState = "Disabled";
        }
        x.OngoingTasks = [sink];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    return <OngoingTasksPage />;
};

export const RabbitSinkDisabled = boundCopy(RabbitSinkTemplate, {
    disabled: true,
});

export const RabbitSinkCompleted = boundCopy(RabbitSinkTemplate, {});

export const ReplicationSinkTemplate = (args: {
    disabled?: boolean;
    customizeTask?: (x: OngoingTaskPullReplicationAsSink) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const sinkListItem = TasksStubs.getReplicationSink();
        if (args.disabled) {
            sinkListItem.TaskState = "Disabled";
        }
        args.customizeTask?.(sinkListItem);
        x.OngoingTasks = [sinkListItem];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    return <OngoingTasksPage />;
};

export const ReplicationSinkDisabled = boundCopy(ReplicationSinkTemplate, {
    disabled: true,
});

export const ReplicationSinkEnabled = boundCopy(ReplicationSinkTemplate, {
    disabled: false,
});

export const ReplicationHubTemplate = (args: {
    disabled?: boolean;
    withOutConnections?: boolean;
    customizeTask?: (x: OngoingTaskPullReplicationAsHub) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const listItem = TasksStubs.getReplicationHub();
        if (args.disabled) {
            listItem.TaskState = "Disabled";
        }

        x.PullReplications.forEach((definition) => {
            definition.Disabled = args.disabled;
        });

        args.customizeTask?.(listItem);
        x.OngoingTasks = args.withOutConnections ? [] : [listItem];
        x.PullReplications = x.PullReplications.filter((x) =>
            args.withOutConnections ? x.Name === "EmptyHub" : x.Name !== "EmptyHub"
        );
        x.SubscriptionsCount = 0;
    });

    mockExternalReplicationProgress(tasksService, true);

    return <OngoingTasksPage />;
};

export const ReplicationHubDisabled = boundCopy(ReplicationHubTemplate, {
    disabled: true,
});

export const ReplicationHubEnabled = boundCopy(ReplicationHubTemplate, {
    disabled: false,
});

export const ReplicationHubNoConnections = boundCopy(ReplicationHubTemplate, {
    disabled: false,
    withOutConnections: true,
});

export const PeriodicBackupTemplate = (args: {
    disabled?: boolean;
    customizeTask?: (x: OngoingTaskBackup) => void;
}) => {
    commonInit();

    const { tasksService } = mockServices;

    tasksService.withGetTasks((x) => {
        const ongoingTask = TasksStubs.getPeriodicBackupListItem();
        if (args.disabled) {
            ongoingTask.TaskState = "Disabled";
        }
        args.customizeTask?.(ongoingTask);
        x.OngoingTasks = [ongoingTask];
        x.PullReplications = [];
        x.SubscriptionsCount = 0;
    });

    return <OngoingTasksPage />;
};

export const PeriodicBackupDisabled = boundCopy(PeriodicBackupTemplate, {
    disabled: true,
});

export const PeriodicBackupEnabledEncrypted = boundCopy(PeriodicBackupTemplate, {
    disabled: false,
    customizeTask: (x) => (x.IsEncrypted = true),
});

export function mockExternalReplicationProgress(tasksService: MockTasksService, completed: boolean) {
    if (completed) {
        tasksService.withGetExternalReplicationProgress((dto) => {
            dto.Results.forEach((x) => {
                x.ProcessesProgress.forEach((progress) => {
                    progress.Completed = true;
                    progress.NumberOfAttachmentsToProcess = 0;
                    progress.NumberOfCounterGroupsToProcess = 0;
                    progress.NumberOfDocumentsToProcess = 0;
                    progress.NumberOfDocumentTombstonesToProcess = 0;
                    progress.NumberOfRevisionsToProcess = 0;
                    progress.NumberOfTimeSeriesSegmentsToProcess = 0;
                    progress.NumberOfTimeSeriesDeletedRangesToProcess = 0;
                });
            });
        });
    } else {
        tasksService.withGetExternalReplicationProgress();
    }
}

export function mockEtlProgress(
    tasksService: MockTasksService,
    completed: boolean,
    disabled: boolean,
    emptyScript: boolean
) {
    if (completed) {
        tasksService.withGetEtlProgress((dto) => {
            dto.Results.forEach((x) => {
                x.ProcessesProgress.forEach((progress) => {
                    progress.Completed = true;
                    progress.Disabled = disabled;
                    progress.NumberOfDocumentsToProcess = 0;
                    progress.NumberOfTimeSeriesSegmentsToProcess = 0;
                    progress.NumberOfTimeSeriesDeletedRangesToProcess = 0;
                    progress.NumberOfCounterGroupsToProcess = 0;
                    progress.NumberOfDocumentTombstonesToProcess = 0;
                    if (emptyScript) {
                        progress.TotalNumberOfDocuments = 0;
                        progress.TotalNumberOfTimeSeriesDeletedRanges = 0;
                        progress.TotalNumberOfTimeSeriesSegments = 0;
                        progress.TotalNumberOfDocumentTombstones = 0;
                        progress.TotalNumberOfCounterGroups = 0;
                    }
                });
            });
        });
    } else {
        tasksService.withGetEtlProgress((dto) => {
            dto.Results.forEach((x) => {
                x.ProcessesProgress.forEach((progress) => {
                    progress.Disabled = disabled;
                });
            });
        });
    }
}
