/**
 * Hosts a README image may be loaded from.
 *
 * This is nuget.org's own list, copied from
 * `src/NuGetGallery/App_Data/Files/Content/Trusted-Image-Domains.json` in NuGet/NuGetGallery. The
 * gallery renders the same READMEs against the same allowlist, so a badge that shows on the package
 * page shows here, and one that does not is one nobody sees anyway.
 *
 * An allowlist rather than "any https host" because a README is written by a stranger: every image
 * it names is a request to a URL of the author's choosing, carrying the reader's IP and the fact
 * that they were looking at that package. These hosts are badge and CDN services, not trackers.
 * Refresh from upstream when a widely used badge service is missing.
 */
export const TrustedImageHosts: readonly string[] = [
    'api.codacy.com',
    'app.codacy.com',
    'api.codeclimate.com',
    'app.deepsource.com',
    'api.dependabot.com',
    'api.travis-ci.com',
    'api.reuse.software',
    'badgen.net',
    'badges.gitter.im',
    'caniuse.bitsofco.de',
    'cdn.jsdelivr.net',
    'cdn.syncfusion.com',
    'ci.appveyor.com',
    'circleci.com',
    'cloudback.it',
    'codecov.io',
    'codefactor.io',
    'coveralls.io',
    'dev.azure.com',
    'devpod.sh',
    'flat.badgen.net',
    'gitlab.com',
    'img.shields.io',
    'infragistics.com',
    'i.imgur.com',
    'isitmaintained.com',
    'media.githubusercontent.com',
    'opencollective.com',
    'snyk.io',
    'sonarcloud.io',
    'travis-ci.com',
    'travis-ci.org',
    'avatars.githubusercontent.com',
    'raw.github.com',
    'raw.githubusercontent.com',
    'user-images.githubusercontent.com',
    'camo.githubusercontent.com',
];
