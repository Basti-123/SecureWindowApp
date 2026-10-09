const menuToggle = document.querySelector('.menu-toggle');
const nav = document.querySelector('#main-nav');
const feedback = document.querySelector('#site-feedback');
const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const canHover = window.matchMedia('(hover: hover)').matches;
const navMenus = [...document.querySelectorAll('.nav-menu')];
const platformSelect = document.querySelector('[data-platform-select]');
const platformLabel = document.querySelector('#platform-label');
const platformChoices = [...document.querySelectorAll('.platform-choice[data-platform]')];
const navCloseTimers = new WeakMap();
let platformCloseTimer;
let platformCloseAnimationTimer;
let feedbackTimer;

const cookieOverlay = document.querySelector('#cookie-consent');
const cookieSettings = document.querySelector('#cookie-settings');
const cookieSettingsToggle = document.querySelector('.cookie-settings-toggle');
const cookieConsentKey = 'veyra-cookie-consent-v1';

const finishCookieChoice = (choice) => {
    try {
        window.localStorage.setItem(cookieConsentKey, choice);
    } catch {
        // The page still works when local storage is unavailable.
    }
    if (!cookieOverlay) return;
    cookieOverlay.hidden = true;
    document.body.classList.remove('cookie-open');
};

if (cookieOverlay) {
    let savedCookieChoice = null;
    try {
        savedCookieChoice = window.localStorage.getItem(cookieConsentKey);
    } catch {
        savedCookieChoice = null;
    }

    if (!savedCookieChoice) {
        cookieOverlay.hidden = false;
        document.body.classList.add('cookie-open');
        window.setTimeout(() => cookieOverlay.querySelector('[data-cookie-choice]')?.focus(), 0);
    }

    cookieOverlay.querySelectorAll('[data-cookie-choice]').forEach((button) => {
        button.addEventListener('click', () => finishCookieChoice(button.dataset.cookieChoice || 'necessary'));
    });

    cookieSettingsToggle?.addEventListener('click', () => {
        const showSettings = cookieSettings?.hidden ?? true;
        if (cookieSettings) cookieSettings.hidden = !showSettings;
        cookieSettingsToggle.setAttribute('aria-expanded', String(showSettings));
        cookieSettingsToggle.textContent = showSettings ? 'Einstellungen schließen' : 'Einstellungen anzeigen';
    });
}

const closeMenus = () => {
    navMenus.forEach((menu) => {
        menu.open = false;
    });
    if (platformSelect) {
        window.clearTimeout(platformCloseTimer);
        window.clearTimeout(platformCloseAnimationTimer);
        platformSelect.open = false;
        platformSelect.classList.remove('is-hovering', 'is-closing');
    }
};

const setMenuState = (open) => {
    nav?.classList.toggle('is-open', open);
    document.body.classList.toggle('menu-open', open);
    menuToggle?.setAttribute('aria-expanded', String(open));
    menuToggle?.setAttribute('aria-label', open ? 'Menü schließen' : 'Menü öffnen');
    if (!open) closeMenus();
};

menuToggle?.addEventListener('click', () => {
    setMenuState(!nav?.classList.contains('is-open'));
});

document.querySelectorAll('.nav-menu, .platform-select').forEach((menu) => {
    menu.addEventListener('toggle', () => {
        if (!menu.open) return;
        document.querySelectorAll('.nav-menu, .platform-select').forEach((otherMenu) => {
            if (otherMenu !== menu) otherMenu.open = false;
        });
    });
});

const setPlatformDownloadState = (platform) => {
    const available = platform === 'Windows';
    document.querySelectorAll('[data-download-button]').forEach((button) => {
        const label = button.querySelector('.button-label');
        if (!button.dataset.originalHref && button.getAttribute('href')) {
            button.dataset.originalHref = button.getAttribute('href');
        }

        if (available) {
            if (button.dataset.originalHref) button.setAttribute('href', button.dataset.originalHref);
            button.removeAttribute('aria-disabled');
            button.classList.remove('is-disabled');
            if (label) label.textContent = button.dataset.defaultLabel || 'Download';
        } else {
            button.removeAttribute('href');
            button.setAttribute('aria-disabled', 'true');
            button.classList.add('is-disabled');
            if (label) label.textContent = `${platform} folgt`;
        }
    });
};

const updatePlatformChoices = (selectedPlatform) => {
    const selectedIndex = platformChoices.findIndex((choice) => choice.dataset.platform === selectedPlatform);
    if (selectedIndex < 0) return;

    platformChoices.forEach((choice, index) => {
        const relativePosition = (index - selectedIndex + platformChoices.length) % platformChoices.length;
        const position = relativePosition === 0 ? 'center' : relativePosition === 1 ? 'right' : 'left';
        choice.classList.remove('is-left', 'is-center', 'is-right');
        choice.classList.add(`is-${position}`);
        choice.style.order = position === 'left' ? '1' : position === 'center' ? '2' : '3';
        choice.setAttribute('aria-selected', String(position === 'center'));
    });
};

updatePlatformChoices(platformLabel?.textContent.trim() || 'Windows');

if (platformSelect) {
    const platformSummary = platformSelect.querySelector('summary');
    const openPlatform = () => {
        window.clearTimeout(platformCloseTimer);
        window.clearTimeout(platformCloseAnimationTimer);
        platformSelect.open = true;
        platformSelect.classList.remove('is-closing');
        platformSelect.classList.add('is-hovering');
    };

    const closePlatform = ({ keepOpenWhileHovered = false, keepOpenWithFocus = false } = {}) => {
        window.clearTimeout(platformCloseAnimationTimer);
        if (!platformSelect.open) return;

        platformSelect.classList.add('is-closing');
        platformCloseAnimationTimer = window.setTimeout(() => {
            if ((keepOpenWhileHovered && platformSelect.matches(':hover')) || (keepOpenWithFocus && platformSelect.matches(':focus-within'))) {
                platformSelect.classList.remove('is-closing');
                return;
            }

            platformSelect.open = false;
            platformSelect.classList.remove('is-hovering', 'is-closing');
        }, 460);
    };

    const schedulePlatformClose = () => {
        window.clearTimeout(platformCloseTimer);
        platformCloseTimer = window.setTimeout(() => {
            if (!platformSelect.matches(':hover')) closePlatform();
        }, 280);
    };

    platformSummary?.addEventListener('click', (event) => {
        event.preventDefault();
        openPlatform();
    });

    platformSelect.addEventListener('pointerenter', () => {
        if (canHover) openPlatform();
    });

    platformSelect.addEventListener('pointerleave', () => {
        if (canHover) schedulePlatformClose();
    });

    platformSelect.addEventListener('focusin', openPlatform);
    platformSelect.addEventListener('focusout', (event) => {
        if (!platformSelect.contains(event.relatedTarget)) {
            closePlatform({ keepOpenWhileHovered: true, keepOpenWithFocus: true });
        }
    });

    platformChoices.forEach((choice) => {
        choice.addEventListener('click', () => {
            const selectedPlatform = choice.dataset.platform || 'Windows';
            if (platformLabel) platformLabel.textContent = selectedPlatform;
            updatePlatformChoices(selectedPlatform);
            setPlatformDownloadState(selectedPlatform);
            closePlatform();

            if (selectedPlatform !== 'Windows' && feedback) {
                feedback.textContent = `${selectedPlatform} ist für den nächsten Veyra-Release vorgemerkt.`;
                feedback.classList.add('is-visible');
                window.clearTimeout(feedbackTimer);
                feedbackTimer = window.setTimeout(() => feedback.classList.remove('is-visible'), 4200);
            }
        });
    });
}

if (canHover) {
    navMenus.forEach((menu) => {
        menu.addEventListener('pointerenter', () => {
            window.clearTimeout(navCloseTimers.get(menu));
            navMenus.forEach((otherMenu) => {
                if (otherMenu !== menu) otherMenu.open = false;
            });
            menu.open = true;
        });

        menu.addEventListener('pointerleave', () => {
            const timer = window.setTimeout(() => {
                if (!menu.matches(':hover')) menu.open = false;
            }, 280);
            navCloseTimers.set(menu, timer);
        });
    });
}

navMenus.forEach((menu) => {
    menu.addEventListener('focusin', () => {
        menu.open = true;
    });

    menu.addEventListener('focusout', (event) => {
        if (!menu.contains(event.relatedTarget)) menu.open = false;
    });
});

nav?.querySelectorAll('a').forEach((link) => {
    link.addEventListener('click', () => setMenuState(false));
});

document.addEventListener('click', (event) => {
    if (!event.target.closest('.site-header') && !event.target.closest('.platform-select')) {
        setMenuState(false);
        closeMenus();
    }
});

document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
        setMenuState(false);
        closeMenus();
    }
});

document.querySelectorAll('a[href^="#"]:not([data-coming-soon])').forEach((link) => {
    link.addEventListener('click', (event) => {
        const target = document.querySelector(link.getAttribute('href'));
        if (!target) return;
        event.preventDefault();
        target.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' });
    });
});

document.querySelectorAll('[data-coming-soon]').forEach((link) => {
    link.addEventListener('click', (event) => {
        event.preventDefault();
        closeMenus();
        const label = link.dataset.comingSoon || 'Diese Version';
        if (!feedback) return;
        window.clearTimeout(feedbackTimer);
        feedback.textContent = `${label} ist für den nächsten Veyra-Release vorgemerkt.`;
        feedback.classList.add('is-visible');
        feedbackTimer = window.setTimeout(() => feedback.classList.remove('is-visible'), 4200);
    });
});

const downloadFeedback = document.querySelector('#download-feedback');

document.querySelectorAll('[data-download-button]').forEach((downloadButton) => {
    const downloadLabel = downloadButton.querySelector('.button-label');
    const defaultLabel = downloadButton.dataset.defaultLabel || 'Download';

    downloadButton.addEventListener('click', (event) => {
        if (downloadButton.getAttribute('aria-disabled') === 'true' || !downloadButton.getAttribute('href')) {
            event.preventDefault();
            return;
        }
        const payload = JSON.stringify({
            version: downloadButton.dataset.version || '1.4.1',
            platform: downloadButton.dataset.platform || 'Windows',
        });
        try {
            window.fetch('/api/downloads', {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: payload,
                keepalive: true,
            }).catch(() => {});
        } catch {
            // The download itself must still work when analytics is unavailable.
        }
        downloadButton.classList.remove('is-complete');
        downloadButton.classList.add('is-starting');
        if (downloadLabel) downloadLabel.textContent = 'Download startet …';
        if (downloadFeedback) downloadFeedback.textContent = 'Der Windows-Download wurde gestartet.';

        window.setTimeout(() => {
            downloadButton.classList.remove('is-starting');
            downloadButton.classList.add('is-complete');
            if (downloadLabel) downloadLabel.textContent = 'Download gestartet';
        }, 650);

        window.setTimeout(() => {
            downloadButton.classList.remove('is-complete');
            if (downloadLabel) downloadLabel.textContent = defaultLabel;
        }, 2600);
    });
});

const newsletterForm = document.querySelector('#newsletter-form');
const newsletterFeedback = document.querySelector('#newsletter-feedback');

newsletterForm?.addEventListener('submit', (event) => {
    event.preventDefault();
    if (newsletterFeedback) {
        newsletterFeedback.textContent = 'Demo-Modus: Es wurden keine Daten übertragen.';
    }
    newsletterForm.reset();
});
