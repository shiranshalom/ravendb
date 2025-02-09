﻿/// <reference path="../../../../typings/tsd.d.ts"/>
import jsonUtil = require("common/jsonUtil");
import getFolderPathOptionsCommand = require("commands/resources/getFolderPathOptionsCommand");
import genUtils = require("common/generalUtils");
import activeDatabaseTracker = require("common/shell/activeDatabaseTracker");

export type configurationOrigin = "Default" | "Server" | "Database";

function findMatchingKey(availableKeys: string[], values: Record<string, Raven.Server.Config.ConfigurationEntrySingleValue>): string | undefined {
    const objectKeys = Object.keys(values);
    return availableKeys.find((key) => objectKeys.includes(key));
}

export abstract class settingsEntry<T extends Raven.Server.Config.ConfigurationEntryServerValue = Raven.Server.Config.ConfigurationEntryServerValue>{

    data: T;

    keyName = ko.observable<string>();
    
    showEntry = ko.observable<boolean>();
    entryMatchesFilter = ko.observable<boolean>();

    isServerWideOnlyEntry = ko.observable<boolean>();
    serverOrDefaultValue: KnockoutComputed<string>;
    hasServerValue: KnockoutComputed<boolean>;
    
    effectiveValue: KnockoutComputed<string>;
    effectiveValueOrigin: KnockoutComputed<configurationOrigin>;
    
    rawValue = ko.observable<string>();
    hasPendingContent = ko.observable<boolean>(false);
    
    hasAccess = ko.observable<boolean>(true);
    isSecured = ko.observable<boolean>(false);
    
    static passwordBullets = "&bull;&bull;&bull;&bull;&bull;";
    
    // These 2 are needed for Summary view
    pendingValueText: KnockoutComputed<string>;
    effectiveValueInUseText: KnockoutComputed<string>;

    entryClassForSummaryMode: KnockoutComputed<string>;
    descriptionHtml: KnockoutComputed<string>;

    descriptionText: KnockoutComputed<string>;

    protected constructor(data: T) {
        this.data = data;

        this.hasServerValue = ko.pureComputed(() => !_.isEmpty(this.data.ServerValues));

        this.keyName(this.data.Metadata.Keys[0]);

        this.isSecured(this.data.Metadata.IsSecured);
        
        if (this.data.Metadata.Keys.length > 1 && this.hasServerValue()) {
            const matchingKey = findMatchingKey(this.data.Metadata.Keys, this.data.ServerValues);
            this.keyName(matchingKey);
        }

        this.serverOrDefaultValue = ko.pureComputed(() => this.hasServerValue() ? this.data.ServerValues[this.keyName()].Value : this.data.Metadata.DefaultValue);

        this.descriptionHtml = ko.pureComputed(() => {
            const rawDescription = data.Metadata.Description;
            return rawDescription ?
                `<div>${genUtils.escapeHtml(rawDescription)}</div>` :
                `<div class="text-muted">No description is available</div>`;
        });

        this.descriptionText = ko.pureComputed(() => {
            const description = data.Metadata.Description;
            return description ?
                genUtils.escapeHtml(description) : "No description is available";
        });

        this.entryClassForSummaryMode = ko.pureComputed(() => {
            return this.hasPendingContent() ||
                   this.effectiveValueOrigin() === "Database" ? "highlight-key" : "";
        });
    }

    static getEntry(rawEntry: Raven.Server.Config.ConfigurationEntryValue) {
        if (rawEntry.Metadata.Scope === "ServerWideOnly") {
            return new serverWideOnlyEntry(rawEntry as Raven.Server.Config.ConfigurationEntryServerValue);
        }
        
        return databaseEntry.getEntry(rawEntry as Raven.Server.Config.ConfigurationEntryDatabaseValue);
    }

    abstract getTemplateType(): settingsTemplateType;
}

export class serverWideOnlyEntry extends settingsEntry {
    constructor(data: Raven.Server.Config.ConfigurationEntryServerValue) {
        super(data);
        this.isServerWideOnlyEntry(true);

        this.effectiveValue = ko.pureComputed(() => this.hasAccess() ? this.serverOrDefaultValue() : settingsEntry.passwordBullets);
        this.effectiveValueOrigin = ko.pureComputed(() => this.hasServerValue() ? "Server" : "Default");
        
        this.pendingValueText = ko.pureComputed(() => "");
        this.effectiveValueInUseText = ko.pureComputed(() => this.effectiveValue());

        const serverValuesHasContent = !_.isEmpty(this.data.ServerValues);
        if (serverValuesHasContent) {
            const keyContent = this.data.ServerValues[this.keyName()];
            if (!keyContent) {
                // we don't expect to get here..
                console.warn(`Key with name: ${this.keyName()} was not found in the Server settings. Server Values are: ${Object.keys(this.data.ServerValues)}`);
            }
            this.hasAccess(keyContent.HasAccess);
        }
    }

    getTemplateType(): settingsTemplateType {
        return "ServerWide";
    }
}

export abstract class databaseEntry<T> extends settingsEntry<Raven.Server.Config.ConfigurationEntryDatabaseValue> {
    customizedDatabaseValue = ko.observable<T>();
    override = ko.observable<boolean>(false);
  
    valueIsPendingDeletion = ko.observable<boolean>(false);

    entryDirtyFlag: () => DirtyFlag;
    validationGroup: KnockoutValidationGroup;

    static getEntry(rawEntry: Raven.Server.Config.ConfigurationEntryDatabaseValue) {
        let entry: databaseEntry<string | number>;
        
        switch (rawEntry.Metadata.Type) {
            case "String":
                entry = new stringEntry(rawEntry);
                break;
            case "Path":
                entry = new pathEntry(rawEntry);
                break;
            case "Integer":
                entry = new integerEntry(rawEntry);
                break;
            case "Double":
                entry = new doubleEntry(rawEntry);
                break;
            case "Boolean":
                entry = new booleanEntry(rawEntry);
                break;
            case "Enum":
                entry = new enumEntry(rawEntry);
                break;
            case "Time":
                entry = new timeEntry(rawEntry);
                break;
            case "Size":
                entry = new sizeEntry(rawEntry);
                break;
            default:
                throw new Error("Unknown entry type: " + rawEntry.Metadata.Type);
        }
        
        entry.init();
        return entry;
    }

    init() {
        this.isServerWideOnlyEntry(false);

        const databaseValuesHasContent = !_.isEmpty(this.data.DatabaseValues);
        
        if (databaseValuesHasContent) {
            const databaseValuesKey = findMatchingKey(this.data.Metadata.Keys, this.data.DatabaseValues);

            const keyContent = this.data.DatabaseValues[databaseValuesKey];
            
            if (keyContent.HasValue) {
                this.rawValue(keyContent.Value);
            }
           
            if (keyContent.PendingValue) {
                this.hasPendingContent(keyContent.PendingValue.HasValue || keyContent.PendingValue.ValueDeleted);
                this.valueIsPendingDeletion(keyContent.PendingValue.ValueDeleted);
            }
          
            this.override(!this.valueIsPendingDeletion());
            
            const customizedValue = keyContent.PendingValue && keyContent.PendingValue.HasValue ?
                                    keyContent.PendingValue.Value :
                                    keyContent.Value;
            
            this.initCustomizedValue(customizedValue);
            
        } else {
            this.initCustomizedValue(this.serverOrDefaultValue());
        }

        this.effectiveValue = ko.pureComputed(() => {
            if (!this.hasAccess() || this.isSecured()) {
                return settingsEntry.passwordBullets;
            }
            
            if (this.override()) {
                return this.getCustomizedValueAsString();
            }

            return this.serverOrDefaultValue();
        });

        this.effectiveValueOrigin = ko.pureComputed(() => this.override() ? "Database" : (this.hasServerValue() ? "Server" : "Default"));

        this.pendingValueText = ko.pureComputed(() => {
            if (!this.hasPendingContent()) {
                return "";
            }

            if (this.valueIsPendingDeletion()) {
                return "<Database value deleted>";
            }

            return this.effectiveValue();
        });

        this.effectiveValueInUseText = ko.pureComputed(() => {
            if (!this.hasAccess()) {
                return settingsEntry.passwordBullets;
            }
            
            if (!this.hasPendingContent()) {
                return this.effectiveValue();
            }
            
            return this.rawValue() || this.serverOrDefaultValue();
        });

        this.entryDirtyFlag = new ko.DirtyFlag([
            this.override,
            this.effectiveValue
        ], false, jsonUtil.newLineNormalizingHashFunction);

        this.initValidation();
    }

    useDefaultValue() {
        this.initCustomizedValue(this.data.Metadata.DefaultValue);
    }

    abstract getCustomizedValueAsString(): string;
    abstract initCustomizedValue(value: string): void;
    abstract initValidation(): void;
}

export class stringEntry extends databaseEntry<string> {

    initCustomizedValue(value: string) {
        this.customizedDatabaseValue(value);
    }

    getCustomizedValueAsString(): string {
        return this.customizedDatabaseValue();
    }

    getTemplateType(): settingsTemplateType {
        return "String";
    }

    initValidation() {
        this.validationGroup = ko.validatedObservable({
            customizedDatabaseValue: this.customizedDatabaseValue
        });
    }
}

export class pathEntry extends databaseEntry<string> {
    folderPathOptions = ko.observableArray<string>([]);

    constructor(data: Raven.Server.Config.ConfigurationEntryDatabaseValue) {
        super(data);
        _.bindAll(this, "pathHasChanged");

        this.customizedDatabaseValue.throttle(300).subscribe((newPathValue) => {
            this.getFolderPathOptions(newPathValue);
        });
    }

    initCustomizedValue(value: string) {
        this.customizedDatabaseValue(value);
    }

    getCustomizedValueAsString(): string {
        return this.customizedDatabaseValue();
    }

    getTemplateType(): settingsTemplateType {
        return "Path";
    }

    initValidation() {
        this.customizedDatabaseValue.extend({
            required: {
                onlyIf: () => this.override() && !_.trim(this.customizedDatabaseValue())
            }
        });

        this.validationGroup = ko.validatedObservable({
            customizedDatabaseValue: this.customizedDatabaseValue
        });
    }

    pathHasChanged(pathValue: string) {
        this.customizedDatabaseValue(pathValue);
    }

    getFolderPathOptions(path?: string) {
        getFolderPathOptionsCommand.forServerLocal(path, true, null, activeDatabaseTracker.default.database()) 
            .execute()
            .done((result: Raven.Server.Web.Studio.FolderPathOptions) => {
                this.folderPathOptions(result.List);
            });
    }
}

export abstract class numberEntry extends databaseEntry<number | null> {
    isNullable = ko.observable<boolean>(this.data.Metadata.IsNullable);
    minValue = ko.observable<number>(this.data.Metadata.MinValue);

    getCustomizedValueAsString(): string {
        if (this.isNullable() && !this.customizedDatabaseValue() && this.customizedDatabaseValue() !== 0) {
            return null;
        } // i.e. for Indexing.MapBatchSize to indicate 'no limit'

        const numberValue = this.customizedDatabaseValue();
        
        if (numberValue) {
            return numberValue >= this.minValue() ? numberValue.toString() : null;
        } else {
            return null;
        }
    }

    initValidation() {
        this.customizedDatabaseValue.extend({
            required: {
                onlyIf: () => this.override() &&
                    !this.isNullable() &&
                    !this.customizedDatabaseValue()
            },
            validation: [
                {
                    validator: (value: number) => (value >= this.minValue() ||
                        (this.isNullable() && !value && value !== 0)),
                    message: "Please enter a value greater or equal to {0}",
                    params: this.minValue()
                }
            ]
        });

        this.validationGroup = ko.validatedObservable({
            customizedDatabaseValue: this.customizedDatabaseValue
        });
    }
}

export class integerEntry extends numberEntry {

    initCustomizedValue(value: string) {
        const integerValue = value ? parseInt(value) : null;
        this.customizedDatabaseValue(integerValue);
    }

    getTemplateType(): settingsTemplateType {
        return "Integer";
    }

    initValidation() {
        super.initValidation();

        this.customizedDatabaseValue.extend({
            digit: true
        });
    }
}

export class doubleEntry extends numberEntry {

    initCustomizedValue(value: string) {
        const doubleValue = value ? parseFloat(value) : null;
        this.customizedDatabaseValue(doubleValue);
    }

    getTemplateType(): settingsTemplateType {
        return "Double";
    }

    initValidation() {
        super.initValidation();

        this.customizedDatabaseValue.extend({
            number: true
        });
    }
}

export class sizeEntry extends numberEntry {
    sizeUnit = ko.observable<string>(this.data.Metadata.SizeUnit);

    initCustomizedValue(value: string) {
        const sizeValue = value ? parseInt(value) : null;
        this.customizedDatabaseValue(sizeValue);
    }

    getTemplateType(): settingsTemplateType {
        return "Size";
    }

    initValidation() {
        super.initValidation();

        this.customizedDatabaseValue.extend({
            digit: true
        });
    }
}

export class timeEntry extends numberEntry {
    timeUnit = ko.observable<string>();

    initCustomizedValue(value: string) {
        const timeValue = value ? parseInt(value) : null;
        this.customizedDatabaseValue(timeValue);

        this.timeUnit(this.data.Metadata.TimeUnit);
        this.minValue(this.data.Metadata.MinValue || -1);
    }

    getTemplateType(): settingsTemplateType {
        return "Time";
    }

    initValidation() {
        super.initValidation();

        this.customizedDatabaseValue.extend({
            digit: {
                onlyIf: () => this.customizedDatabaseValue() !== -1
            }
        });
    }
}

export class enumEntry extends databaseEntry<string> {
    availableValues = ko.observableArray<string>(this.data.Metadata.AvailableValues);

    constructor(data: Raven.Server.Config.ConfigurationEntryDatabaseValue) {
        super(data);
        _.bindAll(this, "initCustomizedValue");
    }

    initCustomizedValue(value: string) {
        this.customizedDatabaseValue(value);
    }

    getCustomizedValueAsString(): string {
        return this.customizedDatabaseValue();
    }

    getTemplateType(): settingsTemplateType {
        return "Enum";
    }

    initValidation() {
        this.customizedDatabaseValue.extend({
            required: {
                onlyIf: () => this.override()
            }
        });

        this.validationGroup = ko.validatedObservable({
            customizedDatabaseValue: this.customizedDatabaseValue
        });
    }
}

export class booleanEntry extends enumEntry {

    constructor(data: Raven.Server.Config.ConfigurationEntryDatabaseValue) {
        super(data);
        this.availableValues(["True", "False"]);
    }
}
