// Load real embedded assets in dependency order using Jellyfin's server/base-path URL.
(function () {
    'use strict';
    window.MediaDash = window.MediaDash || {};
    window.MediaDash.bootstrap = function (root, apiClient) {
        var styles = ['a11y', 'jump', 'tokens', 'layout', 'masthead', 'tabs', 'files',
            'recycle', 'errors', 'overview', 'issues', 'settings', 'wizard', 'history'];
        var scripts = ['page-lifetime', 'api-client', 'dom-helpers', 'library-breakdown',
            'settings-mapper', 'history-tab', 'recycle-tab', 'issues-tab', 'errors-tab', 'diagnostics-tab',
            'overview-tab', 'settings-tab', 'wizard', 'files-tab', 'page-controller'];
        function load(name, style) {
            return new Promise(function (resolve, reject) {
                var el = document.createElement(style ? 'link' : 'script');
                if (style) { el.rel = 'stylesheet'; el.href = apiClient.getUrl('MediaDash/Asset/' + name); }
                else { el.src = apiClient.getUrl('MediaDash/Asset/' + name); el.async = false; }
                el.onload = function () { if (!style) el.remove(); resolve(); };
                el.onerror = function () { el.remove(); reject(new Error('Could not load MediaDash interface: ' + name)); };
                (style ? root : document.head).appendChild(el);
            });
        }
        var chain = Promise.resolve();
        scripts.forEach(function (name) { chain = chain.then(function () { return load(name + '.js', false); }); });
        return Promise.all([chain, Promise.all(styles.map(function (name) { return load(name + '.css', true); }))])
            .then(function () { window.MediaDash.mountPage(root); });
    };
})();
