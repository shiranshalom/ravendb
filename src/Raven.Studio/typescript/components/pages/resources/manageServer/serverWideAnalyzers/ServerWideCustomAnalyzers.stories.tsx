﻿import React from "react";
import { Meta, StoryObj } from "@storybook/react";
import { withStorybookContexts, withBootstrap5, withForceRerender } from "test/storybookTestUtils";
import { mockServices } from "test/mocks/services/MockServices";
import { ManageServerStubs } from "test/stubs/ManageServerStubs";
import { mockStore } from "test/mocks/store/MockStore";
import ServerWideCustomAnalyzers from "components/pages/resources/manageServer/serverWideAnalyzers/ServerWideCustomAnalyzers";

export default {
    title: "Pages/Manage Server/Server-Wide Analyzers",
    decorators: [withStorybookContexts, withBootstrap5, withForceRerender],
    parameters: {
        design: {
            type: "figma",
            url: "https://www.figma.com/design/4EoOO9Zbhiga5CaDFc89Xm/Pages---Server-Wide-Analyzers?node-id=0-1&t=GqrWgI7Qe9PJgbbu-1",
        },
    },
} satisfies Meta;

interface DefaultServerWideCustomAnalyzersProps {
    isEmpty: boolean;
    hasServerWideCustomAnalyzers: boolean;
}

export const ServerWideCustomAnalyzersStory: StoryObj<DefaultServerWideCustomAnalyzersProps> = {
    name: "Server-Wide Analyzers",
    render: (props: DefaultServerWideCustomAnalyzersProps) => {
        const { manageServerService } = mockServices;

        manageServerService.withServerWideCustomAnalyzers(
            props.isEmpty ? [] : ManageServerStubs.serverWideCustomAnalyzers()
        );

        const { license } = mockStore;
        license.with_LicenseLimited({
            HasServerWideAnalyzers: props.hasServerWideCustomAnalyzers,
        });

        return <ServerWideCustomAnalyzers />;
    },
    args: {
        isEmpty: false,
        hasServerWideCustomAnalyzers: true,
    },
};
