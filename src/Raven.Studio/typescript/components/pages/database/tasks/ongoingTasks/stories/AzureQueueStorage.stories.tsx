import { mockServices } from "test/mocks/services/MockServices";
import { TasksStubs } from "test/stubs/TasksStubs";
import { OngoingTasksPage } from "components/pages/database/tasks/ongoingTasks/OngoingTasksPage";
import { userEvent, within } from "@storybook/test";
import React from "react";
import { commonInit, mockEtlProgress } from "components/pages/database/tasks/ongoingTasks/stories/common";
import { Meta, StoryObj } from "@storybook/react";
import { withBootstrap5, withForceRerender, withStorybookContexts } from "test/storybookTestUtils";
import OngoingTaskQueueEtlListView = Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskQueueEtl;
import { MockedValue } from "test/mocks/services/AutoMockService";
import OngoingTasksResult = Raven.Server.Web.System.OngoingTasksResult;

export default {
    title: "Pages/Database/Tasks/Ongoing tasks/Azure Queue Storage",
    decorators: [withStorybookContexts, withBootstrap5, withForceRerender],
} satisfies Meta;

interface AzureQueueStorageProps {
    disabled: boolean;
    completed: boolean;
    customizeTask: (x: OngoingTaskQueueEtlListView) => void;
    emptyScript: boolean;
    runtimeError: boolean;
    loadError: boolean;
    databaseType: "sharded" | "cluster" | "singleNode";
}

export const Default: StoryObj<AzureQueueStorageProps> = {
    render: (args: AzureQueueStorageProps) => {
        commonInit(args.databaseType);

        const { tasksService } = mockServices;

        const mockedValue: MockedValue<OngoingTasksResult> = (x) => {
            const ongoingTask = TasksStubs.getAzureQueueStorageEtl();
            if (args.disabled) {
                ongoingTask.TaskState = "Disabled";
                ongoingTask.TaskConnectionStatus = "NotActive";
            }
            if (args.runtimeError) {
                ongoingTask.Error = "This is some error";
            }
            args.customizeTask?.(ongoingTask);
            x.OngoingTasks = [ongoingTask];
            x.PullReplications = [];
            x.SubscriptionsCount = 0;
        };

        if (args.loadError) {
            tasksService.withThrowingGetTasks((db, location) => location.nodeTag === "C", mockedValue);
        } else {
            tasksService.withGetTasks(mockedValue);
        }

        mockEtlProgress(tasksService, args.completed, args.disabled, args.emptyScript);

        return <OngoingTasksPage />;
    },
    args: {
        completed: true,
        disabled: false,
        runtimeError: false,
        loadError: false,
        emptyScript: false,
        customizeTask: undefined,
        databaseType: "sharded",
    },
    argTypes: {
        databaseType: { control: "radio", options: ["sharded", "cluster", "singleNode"] },
    },
    play: async ({ canvas }) => {
        const container = within(await canvas.findByTestId("azure-queue-storage-etls"));
        await userEvent.click(await container.findByTitle(/Click for details/));
    },
};
export const NotSharded: StoryObj<AzureQueueStorageProps> = {
    ...Default,
    args: {
        ...Default.args,
        databaseType: "cluster",
    },
};

export const Disabled: StoryObj<AzureQueueStorageProps> = {
    ...Default,
    args: {
        ...Default.args,
        disabled: true,
    },
};

export const LoadError: StoryObj<AzureQueueStorageProps> = {
    ...Default,
    args: {
        ...Default.args,
        loadError: true,
    },
};

export const RuntimeError: StoryObj<AzureQueueStorageProps> = {
    ...Default,
    args: {
        ...Default.args,
        runtimeError: true,
    },
};

export const EmptyScript: StoryObj<AzureQueueStorageProps> = {
    ...Default,
    args: {
        ...Default.args,
        completed: true,
        emptyScript: true,
    },
};
