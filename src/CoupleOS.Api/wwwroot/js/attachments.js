// Puts an uploaded file's link where the person was typing.
//
// The server does the upload and returns the markdown; this decides where it
// lands. It has to be here rather than there for the reason the textarea holds
// the whole file: the server never sees the caret, and appending to the end of
// the text would put a receipt at the bottom of a page-long note instead of
// beside the line it is evidence for — which is the difference between an
// attachment that gets linked to an expense and one that gets linked to
// nothing (V0_SCOPE.md).
//
// Vendored beside htmx and served locally. No framework, no CDN (ADR 0010,
// SPEC.md §40).
(function () {
    'use strict';

    document.body.addEventListener('coupleos:attached', function (event) {
        var markdown = event.detail && event.detail.markdown;

        if (!markdown) {
            return;
        }

        var text = document.getElementById('Text');

        if (!text) {
            return;
        }

        var at = typeof text.selectionStart === 'number' ? text.selectionStart : text.value.length;
        var before = text.value.slice(0, at);
        var after = text.value.slice(at);

        // A space in front unless the line is empty or already ends in one, so
        // "AC service 2500" and the link end up on one line and in one block —
        // BlockSegmenter splits on newlines, so a stray one here would make the
        // receipt its own block, and a block with no tool call links to nothing.
        var separator = before === '' || /\s$/.test(before) ? '' : ' ';

        text.value = before + separator + markdown + after;

        var caret = (before + separator + markdown).length;
        text.setSelectionRange(caret, caret);
        text.focus();
    });
})();
