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

    // Initialize
    document.addEventListener('DOMContentLoaded', function () {
        setActiveNav();
        initSidebarToggle();
        renderApiBlocks();
        initAnchors();
        highlightAnchor();

        // Re-highlight on hash change
        window.addEventListener('hashchange', highlightAnchor);
    });
})();
