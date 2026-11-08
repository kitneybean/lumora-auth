// Small site behaviours without inline handlers (the Content-Security-Policy forbids them).
(() => {
    // Mobile navigation: tapping a menu item closes the menu.
    document.addEventListener("click", (e) => {
        if (e.target.closest?.("[data-nav-close]")) {
            const toggler = document.querySelector(".navbar-toggler");
            if (toggler?.checked) toggler.click();
        }
    });
})();
