// FinalShot Documentation - Shared Script

(function () {
    'use strict';

    // Set active nav link based on current page
    function setActiveNav() {
        var path = window.location.pathname;
        var page = path.split('/').pop() || 'welcome.html';
        var links = document.querySelectorAll('.nav-link');
        for (var i = 0; i < links.length; i++) {
            var href = links[i].getAttribute('href');
            if (href === page) {
                links[i].classList.add('active');
            }
        }
    }

    // Mobile sidebar toggle
    function initSidebarToggle() {
        var toggle = document.querySelector('.sidebar-toggle');
        var sidebar = document.querySelector('.sidebar');
        if (!toggle || !sidebar) return;

        toggle.addEventListener('click', function () {
            sidebar.classList.toggle('open');
        });

        // Close sidebar on nav click (mobile)
        var links = sidebar.querySelectorAll('.nav-link');
        for (var i = 0; i < links.length; i++) {
            links[i].addEventListener('click', function () {
                sidebar.classList.remove('open');
            });
        }
    }

    // Create an API block element
    function createApiBlock(name, defaultValue, description, type) {
        var block = document.createElement('div');
        block.className = 'api-block';

        var header = document.createElement('div');
        header.className = 'api-block-header';

        var nameSpan = document.createElement('span');
        nameSpan.className = 'api-name';
        nameSpan.textContent = name;

        var defaultSpan = document.createElement('span');
        defaultSpan.className = 'api-default';
        defaultSpan.textContent = 'Default: ' + defaultValue;

        header.appendChild(nameSpan);
        header.appendChild(defaultSpan);

        var content = document.createElement('div');
        content.className = 'api-block-content';

        if (type) {
            var typeSpan = document.createElement('span');
            typeSpan.className = 'api-type';
            typeSpan.textContent = type;
            content.appendChild(typeSpan);
        }

        var desc = document.createElement('p');
        desc.textContent = description;
        content.appendChild(desc);

        block.appendChild(header);
        block.appendChild(content);

        return block;
    }

    // Render all elements with data-api attributes
    function renderApiBlocks() {
        var elements = document.querySelectorAll('[data-api-name]');
        for (var i = 0; i < elements.length; i++) {
            var el = elements[i];
            var name = el.getAttribute('data-api-name');
            var def = el.getAttribute('data-api-default') || '(none)';
            var type = el.getAttribute('data-api-type') || '';
            var desc = el.getAttribute('data-api-desc') || '';
            var block = createApiBlock(name, def, desc, type);
            el.parentNode.replaceChild(block, el);
        }
    }

    // Inject clickable anchor links into h2/h3 with id and api-blocks
    function initAnchors() {
        var headings = document.querySelectorAll('h2[id], h3[id]');
        for (var i = 0; i < headings.length; i++) {
            var h = headings[i];
            var id = h.getAttribute('id');

            // Wrap existing text content in a link
            var link = document.createElement('a');
            link.className = 'anchor-link';
            link.href = '#' + id;
            link.innerHTML = h.innerHTML;

            // Add the # hash indicator
            var hash = document.createElement('span');
            hash.className = 'anchor-hash';
            hash.textContent = '#';

            h.innerHTML = '';
            h.appendChild(link);
            h.appendChild(hash);

            // Click handler to update URL hash
            (function (anchorId) {
                h.addEventListener('click', function (e) {
                    e.preventDefault();
                    window.location.hash = anchorId;
                    var target = document.getElementById(anchorId);
                    if (target) {
                        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
                    }
                });
            })(id);
        }

        // Add # anchor to each api-block header using the api-name as id
        var apiBlocks = document.querySelectorAll('.api-block');
        for (var j = 0; j < apiBlocks.length; j++) {
            var block = apiBlocks[j];
            var nameEl = block.querySelector('.api-name');
            if (!nameEl) continue;

            var blockId = 'api-' + nameEl.textContent.trim().replace(/[^a-zA-Z0-9]/g, '-');
            block.setAttribute('id', blockId);

            // Add clickable # link next to the property name
            var header = block.querySelector('.api-block-header');
            if (header) {
                var anchor = document.createElement('a');
                anchor.className = 'api-anchor';
                anchor.href = '#' + blockId;
                anchor.textContent = '#';
                anchor.title = 'Link to ' + nameEl.textContent.trim();

                // Wrap name + # in a group so they stay together on the left
                var group = document.createElement('span');
                group.className = 'api-name-group';
                nameEl.parentNode.insertBefore(group, nameEl);
                group.appendChild(nameEl);
                group.appendChild(anchor);

                (function (bid) {
                    header.addEventListener('click', function (e) {
                        // Don't trigger if clicking an internal link
                        if (e.target.tagName === 'A') return;
                        e.preventDefault();
                        window.location.hash = bid;
                        var target = document.getElementById(bid);
                        if (target) {
                            target.scrollIntoView({ behavior: 'smooth', block: 'start' });
                        }
                    });
                })(blockId);
            }
        }
    }

    // Highlight the currently anchored element
    function highlightAnchor() {
        // Remove previous highlight
        var prev = document.querySelector('.anchor-highlight');
        if (prev) prev.classList.remove('anchor-highlight');

        var hash = window.location.hash;
        if (!hash) return;

        var target = document.querySelector(hash);
        if (target) {
            target.classList.add('anchor-highlight');
        }
    }

    // Copy-to-clipboard buttons for code blocks
    function initCopyButtons() {
        var pres = document.querySelectorAll('pre');
        for (var i = 0; i < pres.length; i++) {
            var pre = pres[i];
            pre.style.position = 'relative';

            var btn = document.createElement('button');
            btn.className = 'copy-btn';
            btn.textContent = 'Copy';
            btn.title = 'Copy to clipboard';

            btn.onclick = function (e) {
                e.preventDefault();
                e.stopPropagation();

                var block = this.parentNode;
                var code = block.querySelector('code');
                var text = code ? code.textContent : block.textContent;
                var self = this;

                // Always use textarea fallback for reliability
                var ta = document.createElement('textarea');
                ta.value = text;
                ta.setAttribute('readonly', '');
                ta.style.cssText = 'position:fixed;left:0;top:0;opacity:0;width:1px;height:1px;padding:0;border:none;outline:none;box-shadow:none;';
                document.body.appendChild(ta);
                ta.focus();
                ta.select();

                var ok = false;
                try {
                    ok = document.execCommand('copy');
                } catch (err) { /* ignore */ }
                document.body.removeChild(ta);

                if (ok) {
                    self.textContent = 'Copied!';
                    self.classList.add('copied');
                    setTimeout(function () {
                        self.textContent = 'Copy';
                        self.classList.remove('copied');
                    }, 2000);
                } else {
                    self.textContent = 'Failed';
                    setTimeout(function () {
                        self.textContent = 'Copy';
                    }, 2000);
                }
            };

            pre.appendChild(btn);
        }
    }

    // ========================================
    // Search bar
    // ========================================
    function initSearch() {
        var input = document.querySelector('.sidebar-search input');
        if (!input) return;

        function highlightText(node, query) {
            if (node.nodeType === 3) {
                var text = node.nodeValue;
                var lower = text.toLowerCase();
                var idx = lower.indexOf(query.toLowerCase());
                if (idx === -1) return false;

                var before = text.substring(0, idx);
                var match = text.substring(idx, idx + query.length);
                var after = text.substring(idx + query.length);

                var span = document.createElement('span');
                span.className = 'search-highlight';
                span.textContent = match;

                var parent = node.parentNode;
                if (before) parent.insertBefore(document.createTextNode(before), node);
                parent.insertBefore(span, node);
                if (after) parent.insertBefore(document.createTextNode(after), node);
                parent.removeChild(node);
                return true;
            }
            if (node.nodeType === 1 && node.nodeName !== 'SCRIPT' && node.nodeName !== 'STYLE' && !node.classList.contains('search-highlight')) {
                var children = Array.prototype.slice.call(node.childNodes);
                for (var i = 0; i < children.length; i++) {
                    highlightText(children[i], query);
                }
            }
            return false;
        }

        function removeHighlights() {
            var marks = document.querySelectorAll('.search-highlight');
            for (var i = marks.length - 1; i >= 0; i--) {
                var parent = marks[i].parentNode;
                parent.replaceChild(document.createTextNode(marks[i].textContent), marks[i]);
                parent.normalize();
            }
        }

        input.addEventListener('input', function () {
            var query = this.value.trim();
            var apiBlocks = document.querySelectorAll('.api-block');
            var catHeaders = document.querySelectorAll('.category-header');

            removeHighlights();

            if (!query) {
                for (var i = 0; i < apiBlocks.length; i++) apiBlocks[i].classList.remove('search-hidden');
                for (var j = 0; j < catHeaders.length; j++) catHeaders[j].classList.remove('search-hidden');
                return;
            }

            for (var m = 0; m < apiBlocks.length; m++) apiBlocks[m].classList.add('search-hidden');

            for (var n = 0; n < apiBlocks.length; n++) {
                if (apiBlocks[n].textContent.toLowerCase().indexOf(query.toLowerCase()) !== -1) {
                    apiBlocks[n].classList.remove('search-hidden');
                    highlightText(apiBlocks[n], query);
                }
            }

            for (var h = 0; h < catHeaders.length; h++) {
                var header = catHeaders[h];
                var sibling = header.nextElementSibling;
                var hasVisible = false;
                while (sibling) {
                    if (sibling.classList.contains('category-header')) break;
                    if (sibling.classList.contains('api-block') && !sibling.classList.contains('search-hidden')) {
                        hasVisible = true;
                        break;
                    }
                    sibling = sibling.nextElementSibling;
                }
                if (hasVisible) {
                    header.classList.remove('search-hidden');
                } else {
                    header.classList.add('search-hidden');
                }
            }
        });
    }

    // ========================================
    // Theme toggle (dark / light)
    // ========================================
    function initThemeToggle() {
        var btn = document.querySelector('.theme-toggle-btn');
        if (!btn) return;

        var saved = localStorage.getItem('fs-docs-theme');
        if (saved === 'light') {
            document.body.classList.add('light');
            btn.classList.add('active');
        }

        btn.addEventListener('click', function () {
            document.body.classList.toggle('light');
            var isLight = document.body.classList.contains('light');
            this.classList.toggle('active', isLight);
            localStorage.setItem('fs-docs-theme', isLight ? 'light' : 'dark');
        });
    }

    // ========================================
    // Back to top button
    // ========================================
    function initBackToTop() {
        var btn = document.querySelector('.back-to-top');
        if (!btn) return;

        window.addEventListener('scroll', function () {
            if (window.scrollY > 300) {
                btn.classList.add('visible');
            } else {
                btn.classList.remove('visible');
            }
        });

        btn.addEventListener('click', function () {
            window.scrollTo({ top: 0, behavior: 'smooth' });
        });
    }

    // Initialize
    document.addEventListener('DOMContentLoaded', function () {
        setActiveNav();
        initSidebarToggle();
        renderApiBlocks();
        initAnchors();
        highlightAnchor();
        initCopyButtons();
        initSearch();
        initThemeToggle();
        initBackToTop();

        window.addEventListener('hashchange', highlightAnchor);
    });
})();
