// Currency mask for inputs marked data-money="brl" | "brl-signed" | "percent".
// Typing digits fills from the right, like a bank app: 1 -> 0,01 ; 12345 -> R$ 123,45.
// Delegated listener, so it also works on inputs Blazor renders later.
(function () {
    function format(el) {
        var mode = el.dataset.money;
        var raw = el.value;
        var negative = mode === 'brl-signed' && raw.indexOf('-') !== -1;
        var digits = raw.replace(/\D/g, '').replace(/^0+(?=\d)/, '');

        if (digits === '') {
            el.value = negative ? '-' : '';
            return;
        }

        while (digits.length < 3) digits = '0' + digits;
        var integer = digits.slice(0, -2).replace(/\B(?=(\d{3})+(?!\d))/g, '.');
        var text = integer + ',' + digits.slice(-2);
        if (mode !== 'percent') text = 'R$ ' + (negative ? '-' : '') + text;
        el.value = text;
        el.setSelectionRange(text.length, text.length);
    }

    document.addEventListener('input', function (event) {
        var el = event.target;
        if (el && el.tagName === 'INPUT' && el.dataset && el.dataset.money) format(el);
    });
})();
