import { Meta, StoryObj } from "@storybook/react";
import { withStorybookContexts, withBootstrap5 } from "test/storybookTestUtils";
import TombstonesState from "./TombstonesState";
import { mockServices } from "test/mocks/services/MockServices";
import { mockStore } from "test/mocks/store/MockStore";

export default {
    title: "Pages/Settings/Tombstones",
    decorators: [withStorybookContexts, withBootstrap5],
    parameters: {
        design: {
            type: "figma",
            url: "https://www.figma.com/design/zwbM8CH7rxtMcf5oiRZ47s/Pages---Tombstones?node-id=0-1&t=8d6FSsVV5Xk3bnyf-1",
        },
    },
} satisfies Meta;

export const Tombstones: StoryObj = {
    render: () => {
        const { databasesService } = mockServices;
        const { databases } = mockStore;

        databasesService.withTombstonesState();
        databases.withActiveDatabase_NonSharded_SingleNode();

        return <TombstonesState />;
    },
};
