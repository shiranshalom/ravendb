﻿import { Meta } from "@storybook/react";
import Steps from "./Steps";
import React, { useState } from "react";
import { withBootstrap5, withStorybookContexts } from "test/storybookTestUtils";
import { Button, Card } from "reactstrap";
import { FlexGrow } from "../FlexGrow";
import { Icon } from "../Icon";

export default {
    title: "Bits/Steps",
    component: Steps,
    decorators: [withStorybookContexts, withBootstrap5],
    parameters: {
        design: {
            type: "figma",
            url: "https://www.figma.com/design/ITHbe2U19Ok7cjbEzYa4cb/Design-System-RavenDB-Studio?node-id=15-507",
        },
    },
} satisfies Meta<typeof Steps>;

export function StepsExample() {
    const [currentStep, setCurrentStep] = useState(0);
    const stepsList = [
        {
            label: "Setup",
            isInvalid: true,
        },
        {
            label: "Encryption",
            isInvalid: true,
        },
        {
            label: "Replication & Sharding",
            isInvalid: false,
        },
        {
            label: "Manual Node Selection",
            isInvalid: false,
        },
        {
            label: "Paths Configuration",
            isInvalid: true,
        },
    ];

    const isLastStep = stepsList.length - 2 < currentStep;
    const isFirstStep = currentStep < 1;

    const goToStep = (stepNum: number) => {
        setCurrentStep(stepNum);
    };

    const nextStep = () => {
        if (!isLastStep) {
            setCurrentStep(currentStep + 1);
        }
    };

    const prevStep = () => {
        if (!isFirstStep) {
            setCurrentStep(currentStep - 1);
        }
    };

    return (
        <Card className="p-4">
            <h1>Steps</h1>
            <Steps current={currentStep} steps={stepsList} onClick={goToStep} className="mb-4"></Steps>
            <div className="lead d-flex justify-content-center align-items-center">
                <div className="m-3">
                    Current step: <strong>{currentStep + 1}</strong> / {stepsList.length}
                </div>
                |
                <div className="m-3">
                    First step:{" "}
                    {isFirstStep ? (
                        <strong className="text-success">True</strong>
                    ) : (
                        <strong className="text-danger">False</strong>
                    )}
                </div>
                |
                <div className="m-3">
                    Last step:{" "}
                    {isLastStep ? (
                        <strong className="text-success">True</strong>
                    ) : (
                        <strong className="text-danger">False</strong>
                    )}
                </div>
            </div>
            <div className="d-flex my-4">
                {!isFirstStep && (
                    <Button onClick={prevStep}>
                        <Icon icon="arrow-left" /> Back
                    </Button>
                )}
                <FlexGrow />
                {isLastStep ? (
                    <Button color="success">
                        <Icon icon="rocket" /> Finish
                    </Button>
                ) : (
                    <Button color="primary" onClick={nextStep} disabled={isLastStep}>
                        Next <Icon icon="arrow-right" margin="ms-1" />
                    </Button>
                )}
            </div>
        </Card>
    );
}
