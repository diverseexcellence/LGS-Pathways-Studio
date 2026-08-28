using System.Runtime.CompilerServices;

// Lets backend.Tests exercise internal-only helpers (e.g. the STN-diagnostic name comparison
// logic in UploadController) without making them part of the public API surface.
[assembly: InternalsVisibleTo("LgsImpact.Api.Tests")]
