import React from "react";
import { Meta, StoryFn } from "@storybook/react";
import { withStorybookContexts, withBootstrap5 } from "test/storybookTestUtils";
import StudioDatabaseConfiguration from "./StudioDatabaseConfiguration";
import { mockStore } from "test/mocks/store/MockStore";

export default {
    title: "Pages/Settings/Studio Configuration",
    component: StudioDatabaseConfiguration,
    decorators: [withStorybookContexts, withBootstrap5],
    parameters: {
        design: {
            type: "figma",
            url: "https://www.figma.com/design/oLRwBtzo0fN6pVBccyK2kb/Pages---Studio-Configuration?node-id=0-1&t=R8lX6pqEzDWm0R6u-1",
        },
    },
} satisfies Meta<typeof StudioDatabaseConfiguration>;

function commonInit() {
    const { databases } = mockStore;
    databases.withActiveDatabase_NonSharded_SingleNode();
}

export const LicenseAllowed: StoryFn<typeof StudioDatabaseConfiguration> = () => {
    commonInit();

    const { license } = mockStore;
    license.with_License();

    return <StudioDatabaseConfiguration />;
};

export const LicenseRestricted: StoryFn<typeof StudioDatabaseConfiguration> = () => {
    commonInit();

    const { license } = mockStore;
    license.with_LicenseLimited({ HasStudioConfiguration: false });

    return <StudioDatabaseConfiguration />;
};
