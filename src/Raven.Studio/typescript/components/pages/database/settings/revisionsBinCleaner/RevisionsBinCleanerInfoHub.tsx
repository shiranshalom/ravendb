import { AboutViewAnchored, AccordionItemWrapper } from "components/common/AboutView";
import React from "react";

export function RevisionsBinCleanerInfoHub() {
    return (
        <AboutViewAnchored>
            <AccordionItemWrapper
                targetId="about"
                icon="about"
                color="info"
                heading="About this view"
                description="Get additional info on this feature"
            >
                Content
            </AccordionItemWrapper>
        </AboutViewAnchored>
    );
}
