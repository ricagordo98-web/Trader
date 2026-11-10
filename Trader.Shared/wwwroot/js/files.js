export function downloadText(fileName, content, contentType) {
    const blob = new Blob([content], { type: contentType });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export async function createPdfUrl(streamReference) {
    const buffer = await streamReference.arrayBuffer();
    return URL.createObjectURL(new Blob([buffer], { type: 'application/pdf' }));
}

export function revokeUrl(url) {
    URL.revokeObjectURL(url);
}
