import leafMenuItem = require("common/shell/menu/leafMenuItem");
import appUrl = require("common/appUrl");
import reactUtils = require("common/reactUtils");
import DatabasesPage = require("components/pages/resources/databases/DatabasesPage");

export = getDatabasesMenuItem;

function getDatabasesMenuItem(appUrls: computedAppUrls) {
    const databasesView = reactUtils.bridgeToReact(DatabasesPage.DatabasesPage, "nonShardedView");
    
    appUrl.defaultModule = databasesView;
    
    return new leafMenuItem({
        route: "databases",
        title: "Databases",
        moduleId: databasesView,
        nav: true,
        css: 'icon-resources',
        dynamicHash: appUrls.databasesManagement
    });
}
