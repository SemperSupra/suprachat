# Native review-window close evidence — issue #51

The first close request stops consent synchronously and fences pending review, confirmation and
Save operations. Duplicate requests share the same finalization. The window waits up to five seconds
for the current UI operation, then uses the native controller's bounded cleanup path. Pending or
failed work is reported without asserting completed cleanup. Final Close is posted to the dispatcher,
including default-OFF and prepared-only cases, avoiding reentrant Closing behavior.

The aggregate is prepared before the recorder is disposed. A replacement Prepare retains its retiring
owner's sanitized snapshot until the operation fence permits handoff, so close during disposal can
preserve the earlier ending reason and evidence. A completed explicit Save for the same capture is
reused. Actual Closed persists any unsaved prepared aggregate using the configured directory and
unique CreateNew file. No file picker, typed phrase, upload, raw events or authority export is added.
The parent displays a fixed saved/off/failure result after review closure. An unavailable output
directory remains a reported failure, never a successful-save claim.

Synthetic qualification uses a separate schema (`suprachat-synthetic-native-metadata-summary/v1`),
file prefix and evidence label. Its store refuses first-party documents, and the first-party store
refuses synthetic documents. The first-party v2 schema and exact-origin admission remain unchanged.

The actual disposable native fixture exercises active close, pending-enable close, close during
replacement Prepare and Save, preserved expiry reason, duplicate close, prepared/OFF close and output
failure. Aggregate checks require the correct synthetic schema, ending reason, stopped/cleanup state,
one file and absence of private sentinel, URL, header or authority content. Contract checks also
exercise store separation. These are in-process own-window handler tests, not independent UIA of a
signed-in window. The public receipt contains fixed qualification flags, not the aggregate files.

Review-window closure is distinct from main-window/process exit. The main host still does not await
review finalization before dispatcher shutdown; no whole-app shutdown qualification is claimed.
Real signed-in expiry/navigation/Stop/close must still be exercised through the supported UI tool.
