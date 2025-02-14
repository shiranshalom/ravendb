﻿import { Meta } from "@storybook/react";
import React from "react";
import { ProgressCircle } from "./ProgressCircle";
import { boundCopy } from "../utils/common";

export default {
    title: "Bits/Progress circle",
    component: ProgressCircle,
    parameters: {
        design: {
            type: "figma",
            url: "https://www.figma.com/design/ITHbe2U19Ok7cjbEzYa4cb/Design-System-RavenDB-Studio?node-id=11-46",
        },
    },
} satisfies Meta<typeof ProgressCircle>;

const Template = () => {
    return (
        <div>
            <h3>Regular:</h3>

            <ProgressCircle state="success" icon="check">
                OK
            </ProgressCircle>

            <ProgressCircle state="running" icon="pause" progress={0.75}>
                Paused
            </ProgressCircle>

            <ProgressCircle state="running" progress={0.75}>
                Running
            </ProgressCircle>

            <ProgressCircle state="running">Running</ProgressCircle>

            <ProgressCircle state="failed" icon="cancel">
                Error
            </ProgressCircle>

            <h3>Inline</h3>

            <ProgressCircle state="success" icon="check" inline>
                OK
            </ProgressCircle>

            <ProgressCircle state="running" icon="pause" progress={0.75} inline>
                Paused
            </ProgressCircle>

            <ProgressCircle state="running" progress={0.75} inline>
                Running
            </ProgressCircle>

            <ProgressCircle state="running" inline>
                Running
            </ProgressCircle>

            <ProgressCircle state="failed" icon="cancel" inline>
                Error
            </ProgressCircle>
        </div>
    );
};

export const States = boundCopy(Template);
