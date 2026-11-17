// Server-aware requests scoped to the current page generation.
// Stale successes and failures never render; active HTTP errors retain their server explanation.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    ns.MediaDash.createApi = function createApi(lifetime, apiClient) {
        function guard(promise) {
            var token = lifetime.current();
            return promise.then(function (r) {
                if (!lifetime.accepts(token)) return new Promise(function () {});
                return r;
            }, function (error) {
                if (!lifetime.accepts(token)) return new Promise(function () {});
                // Jellyfin versions using fetch reject with a Response, rather than an XHR.
                if (error && typeof error.clone === 'function' && typeof error.text === 'function') {
                    return error.clone().text().catch(function () { return ''; }).then(function (body) {
                        if (!lifetime.accepts(token)) return new Promise(function () {});
                        error.responseText = body;
                        try {
                            var parsed = JSON.parse(body);
                            if (parsed && typeof parsed === 'object') error.responseJSON = parsed;
                        } catch (_) { /* Keep non-JSON server explanations in responseText. */ }
                        throw error;
                    });
                }
                throw error;
            });
        }
        function api(path) { return guard(apiClient.getJSON(apiClient.getUrl('MediaDash/' + path))); }
        api.configuration = function (id) { return guard(apiClient.getPluginConfiguration(id)); };
        api.ajax = function (options) { return guard(apiClient.ajax(options)); };
        return api;
    };
})();
