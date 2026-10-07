// GEN-06: a <form data-confirm="message"> asks for confirmation in the shared Bootstrap modal before submitting.
document.addEventListener('submit', function (e) {
    const form = e.target;
    if (!form.dataset.confirm || form.dataset.confirmed) return;
    e.preventDefault();

    const modalEl = document.getElementById('confirmModal');
    modalEl.querySelector('.modal-body').textContent = form.dataset.confirm;
    const modal = bootstrap.Modal.getOrCreateInstance(modalEl);
    modalEl.querySelector('[data-confirm-ok]').onclick = function () {
        form.dataset.confirmed = '1';
        modal.hide();
        form.requestSubmit();
    };
    modal.show();
});
