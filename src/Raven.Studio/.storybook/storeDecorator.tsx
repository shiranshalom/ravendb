import { resetAllMocks } from "@storybook/test";
import { useState } from "react";
import { createStoreConfiguration } from "components/store";
import { setEffectiveTestStore } from "components/storeCompat";
import { Provider } from "react-redux";

export const StoreDecorator = (Story) => {
    const [store] = useState(() => {
        resetAllMocks();

        const storeConfiguration = createStoreConfiguration();
        setEffectiveTestStore(storeConfiguration);
        return storeConfiguration;
    });

    return (
        <Provider store={store}>
            <div className="h-100">
                <Story />
            </div>
        </Provider>
    );
};
