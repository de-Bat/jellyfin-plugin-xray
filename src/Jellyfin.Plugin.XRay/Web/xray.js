/* X-Ray for Jellyfin: shows who is on screen while a video plays. */
(function () {
    'use strict';

    if (window.__xrayLoaded) {
        return;
    }
    window.__xrayLoaded = true;

    var TICKS_PER_SECOND = 10000000;
    var state = {
        itemId: null,
        data: null,
        loadingId: null,
        offsetTicks: 0,
        offsetFromReport: false,
        pinned: false,
        tab: 'scene',
        isAdmin: null,
        lastSessionPoll: 0,
        lastRenderKey: null,
        analysisTimer: null
    };

    // ---------- helpers ----------

    function apiClient() {
        return window.ApiClient || null;
    }

    function getVideo() {
        var v = document.querySelector('video.htmlvideoplayer') || document.querySelector('.videoPlayerContainer video');
        return v && v.isConnected ? v : null;
    }

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) {
            e.className = cls;
        }
        if (text !== undefined && text !== null) {
            e.textContent = text;
        }
        return e;
    }

    function fetchJson(path, method) {
        var ac = apiClient();
        return ac.ajax({ type: method || 'GET', url: ac.getUrl(path), dataType: 'json' });
    }

    function imageUrl(personId) {
        return apiClient().getUrl('Items/' + personId + '/Images/Primary', { fillHeight: 180, fillWidth: 120, quality: 90 });
    }

    function formatTime(ticks) {
        var s = Math.floor(ticks / TICKS_PER_SECOND);
        var h = Math.floor(s / 3600);
        var m = Math.floor((s % 3600) / 60);
        var sec = s % 60;
        var mm = (h > 0 && m < 10 ? '0' : '') + m;
        return (h > 0 ? h + ':' : '') + mm + ':' + (sec < 10 ? '0' : '') + sec;
    }

    // ---------- which item is playing, and where ----------

    function onPlaybackReport(info) {
        if (!info || !info.ItemId) {
            return;
        }
        setItem(String(info.ItemId).replace(/-/g, ''));
        var video = getVideo();
        if (video && typeof info.PositionTicks === 'number') {
            // Transcoded streams that were seeked start at 0, so learn the offset from the player's own report.
            var offset = info.PositionTicks - Math.round(video.currentTime * TICKS_PER_SECOND);
            state.offsetTicks = Math.abs(offset) < 2 * TICKS_PER_SECOND ? 0 : offset;
            state.offsetFromReport = true;
        }
    }

    function hookReports() {
        var ac = apiClient();
        if (!ac) {
            return;
        }
        var proto = Object.getPrototypeOf(ac);
        if (!proto || proto.__xrayHooked) {
            return;
        }
        ['reportPlaybackStart', 'reportPlaybackProgress'].forEach(function (name) {
            var original = proto[name];
            if (typeof original !== 'function') {
                return;
            }
            proto[name] = function (info) {
                try {
                    onPlaybackReport(info);
                } catch (e) {
                    console.warn('[XRay]', e);
                }
                return original.apply(this, arguments);
            };
        });
        proto.__xrayHooked = true;
    }

    function pollSession() {
        var ac = apiClient();
        if (!ac || Date.now() - state.lastSessionPoll < 5000) {
            return;
        }
        state.lastSessionPoll = Date.now();
        var url = ac.getUrl('Sessions', { deviceId: ac.deviceId() });
        ac.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function (sessions) {
            var session = (sessions || []).filter(function (s) {
                return s.NowPlayingItem;
            })[0];
            if (!session) {
                return;
            }
            setItem(String(session.NowPlayingItem.Id).replace(/-/g, ''));
            var video = getVideo();
            if (!state.offsetFromReport && video && session.PlayState && typeof session.PlayState.PositionTicks === 'number') {
                // Session positions lag by up to ~10s; only trust large offsets (seeked transcodes).
                var offset = session.PlayState.PositionTicks - Math.round(video.currentTime * TICKS_PER_SECOND);
                state.offsetTicks = Math.abs(offset) < 30 * TICKS_PER_SECOND ? 0 : offset;
            }
        }, function () { /* ignore */ });
    }

    function setItem(itemId) {
        if (!itemId || itemId === state.itemId) {
            return;
        }
        state.itemId = itemId;
        state.data = null;
        state.offsetTicks = 0;
        state.offsetFromReport = false;
        state.lastRenderKey = null;
        loadItem(itemId);
    }

    function loadItem(itemId) {
        state.loadingId = itemId;
        fetchJson('XRay/Items/' + itemId).then(function (data) {
            if (state.itemId === itemId) {
                state.data = data;
                state.lastRenderKey = null;
                if (data.Analysis && (data.Analysis.State === 'Queued' || data.Analysis.State === 'Running')) {
                    scheduleAnalysisPoll(itemId);
                }
            }
        }, function (err) {
            console.warn('[XRay] could not load data for', itemId, err);
        });
    }

    function scheduleAnalysisPoll(itemId) {
        clearTimeout(state.analysisTimer);
        state.analysisTimer = setTimeout(function () {
            if (state.itemId === itemId) {
                loadItem(itemId);
            }
        }, 3000);
    }

    function currentPositionTicks(video) {
        return Math.round(video.currentTime * TICKS_PER_SECOND) + state.offsetTicks;
    }

    function peopleAt(ticks) {
        var data = state.data;
        if (!data || !data.HasTimeline) {
            return [];
        }
        var seen = {};
        var ids = [];
        data.Appearances.forEach(function (a) {
            if (a.StartTicks <= ticks && ticks < a.EndTicks && !seen[a.PersonId]) {
                seen[a.PersonId] = true;
                ids.push(a.PersonId);
            }
        });
        var byId = {};
        data.People.forEach(function (p) {
            byId[p.Id] = p;
        });
        // Keep billing order so the panel does not reshuffle while watching.
        return data.People.filter(function (p) {
            return seen[p.Id];
        }).concat(ids.filter(function (id) {
            return !byId[id];
        }).map(function (id) {
            return { Id: id, Name: 'Unknown', Role: null, HasImage: true };
        }));
    }

    function nextAppearance(personId, ticks) {
        var list = (state.data && state.data.Appearances) || [];
        for (var i = 0; i < list.length; i++) {
            if (list[i].PersonId === personId && list[i].StartTicks > ticks) {
                return list[i].StartTicks;
            }
        }
        return null;
    }

    // ---------- UI ----------

    var panel = null;
    var osdButton = null;

    function injectStyles() {
        if (document.getElementById('xray-styles')) {
            return;
        }
        var style = el('style');
        style.id = 'xray-styles';
        style.textContent = [
            '#xray-panel{position:fixed;left:0;top:0;bottom:0;width:min(360px,80vw);z-index:1100;pointer-events:none;',
            'background:linear-gradient(90deg,rgba(0,0,0,.88) 0%,rgba(0,0,0,.72) 70%,rgba(0,0,0,0) 100%);',
            'color:#fff;font-family:inherit;padding:5em 2.5em 7em 1.5em;box-sizing:border-box;opacity:0;transform:translateX(-24px);',
            'transition:opacity .25s ease,transform .25s ease;display:flex;flex-direction:column}',
            '#xray-panel.xray-visible{opacity:1;transform:none;pointer-events:auto}',
            '#xray-panel .xray-head{display:flex;align-items:baseline;gap:1em;margin-bottom:1em}',
            '#xray-panel .xray-logo{font-weight:700;font-size:1.1em;letter-spacing:.04em;color:#00a4dc}',
            '#xray-panel .xray-tab{background:none;border:0;color:rgba(255,255,255,.6);font:inherit;font-size:.95em;cursor:pointer;padding:.2em 0;border-bottom:2px solid transparent}',
            '#xray-panel .xray-tab.xray-active{color:#fff;border-bottom-color:#fff}',
            '#xray-panel .xray-list{overflow-y:auto;flex:1;scrollbar-width:thin}',
            '#xray-panel .xray-card{display:flex;gap:.9em;align-items:center;padding:.45em 0;color:inherit;text-decoration:none}',
            '#xray-panel .xray-card:hover .xray-name{text-decoration:underline}',
            '#xray-panel .xray-img{width:52px;height:78px;border-radius:4px;object-fit:cover;background:#333;flex:none}',
            '#xray-panel .xray-name{font-weight:600;font-size:1em;line-height:1.25}',
            '#xray-panel .xray-role{color:rgba(255,255,255,.65);font-size:.85em;margin-top:.15em}',
            '#xray-panel .xray-next{color:rgba(255,255,255,.45);font-size:.75em;margin-top:.2em}',
            '#xray-panel .xray-empty{color:rgba(255,255,255,.65);font-size:.9em;line-height:1.5}',
            '#xray-panel .xray-action{margin-top:.8em;background:#00a4dc;color:#fff;border:0;border-radius:4px;padding:.5em 1em;font:inherit;cursor:pointer}',
            '#xray-panel .xray-progress{height:4px;background:rgba(255,255,255,.2);border-radius:2px;margin-top:.8em;overflow:hidden}',
            '#xray-panel .xray-progress>div{height:100%;background:#00a4dc;transition:width .5s}',
            '.xray-osd-button{font-weight:700;letter-spacing:.04em;font-size:.85em!important;padding:.4em .7em!important;width:auto!important}',
            '.xray-osd-button.xray-active{color:#00a4dc}'
        ].join('');
        document.head.appendChild(style);
    }

    function ensurePanel() {
        if (panel && panel.isConnected) {
            return panel;
        }
        injectStyles();
        panel = el('div');
        panel.id = 'xray-panel';
        panel.setAttribute('aria-live', 'polite');
        // Keep clicks inside the panel from toggling play/pause on the video.
        ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'dblclick', 'touchstart'].forEach(function (type) {
            panel.addEventListener(type, function (e) {
                e.stopPropagation();
            });
        });
        document.body.appendChild(panel);
        return panel;
    }

    function ensureOsdButton() {
        var bar = document.querySelector('.videoOsdBottom .buttons');
        if (!bar || (osdButton && bar.contains(osdButton))) {
            return;
        }
        osdButton = el('button', 'xray-osd-button paper-icon-button-light autoSize', 'X-Ray');
        osdButton.type = 'button';
        osdButton.title = 'X-Ray (X)';
        osdButton.addEventListener('click', function (e) {
            e.stopPropagation();
            togglePinned();
        });
        bar.insertBefore(osdButton, bar.firstChild);
    }

    function togglePinned() {
        state.pinned = !state.pinned;
        state.lastRenderKey = null;
        tick();
    }

    function isOsdVisible() {
        var osd = document.querySelector('.videoOsdBottom');
        if (!osd) {
            return false;
        }
        if (osd.classList.contains('videoOsdBottom-hidden') || osd.classList.contains('hide')) {
            return false;
        }
        var style = window.getComputedStyle(osd);
        return style.display !== 'none' && style.visibility !== 'hidden' && parseFloat(style.opacity || '1') > 0.1;
    }

    function renderCard(person, ticks, showNext) {
        var a = el('a', 'xray-card');
        a.href = '#/details?id=' + person.Id + (apiClient().serverId ? '&serverId=' + apiClient().serverId() : '');
        a.target = '_blank';
        a.rel = 'noopener';
        var img = el('img', 'xray-img');
        img.alt = '';
        img.loading = 'lazy';
        if (person.HasImage) {
            img.src = imageUrl(person.Id);
        }
        a.appendChild(img);
        var text = el('div');
        text.appendChild(el('div', 'xray-name', person.Name));
        if (person.Role) {
            text.appendChild(el('div', 'xray-role', person.Role));
        }
        if (showNext) {
            var next = nextAppearance(person.Id, ticks);
            if (next !== null) {
                text.appendChild(el('div', 'xray-next', 'Next on screen ' + formatTime(next)));
            }
        }
        a.appendChild(text);
        return a;
    }

    function renderEmpty(list, message) {
        list.appendChild(el('div', 'xray-empty', message));
    }

    function renderAnalysis(list) {
        var data = state.data;
        var analysis = data && data.Analysis;
        if (analysis && (analysis.State === 'Queued' || analysis.State === 'Running')) {
            renderEmpty(list, analysis.State === 'Queued' ? 'X-Ray analysis queued…' : 'Analyzing faces… ' + Math.round(analysis.Progress) + '%');
            var bar = el('div', 'xray-progress');
            var fill = el('div');
            fill.style.width = Math.round(analysis.Progress) + '%';
            bar.appendChild(fill);
            list.appendChild(bar);
            return;
        }
        renderEmpty(list, analysis && analysis.State === 'Failed'
            ? 'X-Ray analysis failed: ' + (analysis.Error || 'unknown error')
            : 'No X-Ray scene data for this video yet.');
        if (state.isAdmin) {
            var btn = el('button', 'xray-action', 'Analyze now');
            btn.addEventListener('click', function () {
                var itemId = state.itemId;
                btn.disabled = true;
                fetchJson('XRay/Items/' + itemId + '/Analyze', 'POST').then(function (status) {
                    if (state.data && state.itemId === itemId) {
                        state.data.Analysis = status;
                        state.lastRenderKey = null;
                        scheduleAnalysisPoll(itemId);
                    }
                }, function () {
                    btn.disabled = false;
                });
            });
            list.appendChild(btn);
        }
    }

    function render(video) {
        var p = ensurePanel();
        var ticks = currentPositionTicks(video);
        var data = state.data;
        var inScene = data ? peopleAt(ticks) : [];
        var analysis = data && data.Analysis;
        var key = [state.itemId, state.tab, !!data, data && data.HasTimeline,
            inScene.map(function (x) { return x.Id; }).join(','),
            analysis ? analysis.State + Math.round(analysis.Progress) : '',
            state.tab === 'scene' ? Math.floor(ticks / (30 * TICKS_PER_SECOND)) : ''].join('|');
        if (key === state.lastRenderKey) {
            return;
        }
        state.lastRenderKey = key;

        p.textContent = '';
        var head = el('div', 'xray-head');
        head.appendChild(el('span', 'xray-logo', 'X-Ray'));
        [['scene', 'In Scene'], ['cast', 'All Cast']].forEach(function (t) {
            var b = el('button', 'xray-tab' + (state.tab === t[0] ? ' xray-active' : ''), t[1]);
            b.type = 'button';
            b.addEventListener('click', function () {
                state.tab = t[0];
                state.lastRenderKey = null;
                tick();
            });
            head.appendChild(b);
        });
        p.appendChild(head);

        var list = el('div', 'xray-list');
        p.appendChild(list);
        if (!data) {
            renderEmpty(list, 'Loading…');
        } else if (state.tab === 'cast') {
            if (!data.People.length) {
                renderEmpty(list, 'No cast information.');
            }
            data.People.forEach(function (person) {
                list.appendChild(renderCard(person, ticks, data.HasTimeline));
            });
        } else if (!data.HasTimeline) {
            renderAnalysis(list);
        } else if (!inScene.length) {
            renderEmpty(list, 'No cast members recognized in this scene.');
        } else {
            inScene.forEach(function (person) {
                list.appendChild(renderCard(person, ticks, false));
            });
        }
    }

    function tick() {
        hookReports();
        var video = getVideo();
        if (!video || !apiClient()) {
            if (panel) {
                panel.classList.remove('xray-visible');
            }
            if (!video && state.itemId) {
                state.itemId = null;
                state.data = null;
                state.pinned = false;
            }
            return;
        }

        if (state.isAdmin === null) {
            state.isAdmin = false;
            apiClient().getCurrentUser().then(function (user) {
                state.isAdmin = !!(user && user.Policy && user.Policy.IsAdministrator);
                state.lastRenderKey = null;
            }, function () { /* ignore */ });
        }

        pollSession();
        ensureOsdButton();
        if (osdButton) {
            osdButton.classList.toggle('xray-active', state.pinned);
        }

        var visible = state.pinned || video.paused || isOsdVisible();
        if (!state.itemId) {
            visible = false;
        }
        if (visible) {
            render(video);
        }
        ensurePanel().classList.toggle('xray-visible', visible);
    }

    document.addEventListener('keydown', function (e) {
        if ((e.key === 'x' || e.key === 'X') && !e.ctrlKey && !e.metaKey && !e.altKey && getVideo()) {
            var target = e.target;
            if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) {
                return;
            }
            togglePinned();
        }
    }, true);

    setInterval(tick, 500);
})();
