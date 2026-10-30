// Sunucudan alınan baytları dosya olarak indirir (oturumsuz müşteri sayfası belgeyi POST ile alır; adres çubuğunda belirteç taşımaz).
window.modulixDownload = function (fileName, contentType, base64) {
    var bytes = Uint8Array.from(atob(base64), function (c) { return c.charCodeAt(0); });
    var url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    var anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 10000);
};
