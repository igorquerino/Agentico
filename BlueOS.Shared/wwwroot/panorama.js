/* Atalhos do Panorama: "/" leva o cursor à busca; Esc fecha o popover e limpa a busca (avisa o Blazor). */
window.blueos = window.blueos || {};
window.blueos.atalhos = function (ref) {
    if (window.blueos._ligado) return;
    window.blueos._ligado = true;
    document.addEventListener("keydown", function (e) {
        var alvo = e.target;
        var digitando = alvo && (alvo.tagName === "INPUT" || alvo.tagName === "TEXTAREA" || alvo.tagName === "SELECT" || alvo.isContentEditable);
        if (e.key === "/" && !digitando) {
            e.preventDefault();
            var b = document.getElementById("busca");
            if (b) { b.focus(); b.select(); }
        } else if (e.key === "Escape") {
            if (digitando && alvo.id === "busca") alvo.blur();
            ref.invokeMethodAsync("Esc");
        }
    });
};
