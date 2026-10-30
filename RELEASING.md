# Releasing Netjes

The npm package `netjes` and the NuGet packages `Netjes` and `Netjes.Images.SkiaSharp` are released together, under one version, by the Release workflow ([.github/workflows/release.yml](.github/workflows/release.yml)). Nothing is published on a push or a tag. A maintainer starts the workflow for a tag and approves the publishing job.

## What the workflow does

1. Checks out the tag it is given, such as `v0.0.1`.
2. Stops unless:
   - the CI workflow passed on `main` for the tag's commit (that run includes both end-to-end suites, the package check and the tests on macOS and Windows);
   - the version in the tag is the version in `packages/netjes/package.json`, its entry in `package-lock.json`, `Directory.Build.props` and the places in the docs that pin it;
   - `CHANGELOG.md` has an entry for that version;
   - `packages/netjes/package.json` names this repository, which npm checks against the provenance statement.
3. Builds, then runs the type checks, the Vitest suites, the generated-types check, the release script's tests and the .NET tests.
4. Packs the three packages, checks that each file name carries the version, and uploads them as the `packages` artifact.
5. Waits for a reviewer to approve the `release` environment, then pushes the NuGet packages to nuget.org (trusted publishing) and publishes the npm package with provenance.
6. Creates the GitHub release for the tag, with the changelog entry as its notes and the packages attached.

Each publishing step skips a version that is already published, so a run that failed halfway can be re-run. The version, pinned-docs, changelog and repository checks also run locally: `node scripts/release.mjs check v0.0.1 --repository DennisVerhoeven/netjes`.

## One-time setup

The workflow does nothing useful until the repository, the environment and the registry accounts exist. All of this happens in the web interfaces; nothing in the repository changes.

### GitHub

1. Create the public repository https://github.com/DennisVerhoeven/netjes and push `main`. CI runs on that push.
2. **Settings → Actions → General**: set the default workflow permissions to read-only. Each workflow asks for the extra permissions it needs.
3. **Settings → Rules → Rulesets**:
   - for `main`: require a pull request and the status checks `build-and-test`, `e2e (Blog)`, `e2e (TaskBoard)` and `packages`, and block force pushes. The jobs on macOS and Windows, `build-and-test (macos-latest)` and `build-and-test (windows-latest)`, are not required here; a release still needs them, since it waits for the whole CI run on `main` to pass;
   - for tags matching `v*`: restrict creation, updates and deletion to maintainers, so a release tag cannot move after its CI run.
4. **Settings → Environments → New environment** `release`:
   - required reviewers: the maintainers who may publish. With a single maintainer, leave "Prevent self-review" off;
   - deployment branches and tags: selected branches, `main` only. The workflow is started from `main`;
   - environment secrets: `NUGET_USER`, the nuget.org profile name (not the e-mail address) of the account that owns the packages, and `NPM_TOKEN` (see npm below).
5. In the repository's security settings, enable private vulnerability reporting, which `SECURITY.md` asks reporters to use, plus Dependabot alerts and Dependabot security updates. Version updates are configured in [.github/dependabot.yml](.github/dependabot.yml).
6. **Settings → General → Features**: turn on Discussions. The README, `CONTRIBUTING.md` and the issue chooser's contact link send questions there, and their links lead nowhere until it is on.
7. **Settings → General → Social preview**: upload [assets/social-preview.png](assets/social-preview.png), the Netjes mark and name at 1280x640, which links to the repository show instead of the owner's avatar. `node assets/render.mjs` makes it from `assets/social-preview.svg`.

### nuget.org

1. Sign in with the account that will own the packages, with two-factor authentication on.
2. **Your profile → Trusted Publishing → Create**: package owner `DennisVerhoeven`, CI/CD provider GitHub Actions, repository owner `DennisVerhoeven`, repository `netjes`, workflow file `release.yml`, environment `release`; scope **Push new packages and package versions**; and under glob patterns and packages, `Netjes` and `Netjes.Images.SkiaSharp`, one per line. A policy covers every package its owner has, so it works for the first push too. The workflow exchanges the job's OIDC token for an API key that is valid for one hour, so there is no NuGet API key to store. If nuget.org shows the policy as temporarily active, a publish has to happen within seven days, or the window has to be restarted.
3. Reserve the `Netjes` ID prefix: e-mail account@nuget.org with the owner's display name, asking for `Netjes` and `Netjes.*`. Once it is reserved, only that owner can push packages whose IDs start with it, and nuget.org marks them as verified. The criteria are in [ID prefix reservation](https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation). Ask for it around the first push.

### npm

npm sets up trusted publishing per package, in the package's settings, so it cannot be set up before `netjes` exists. The first release publishes with a token.

1. Sign in to npmjs.com with the account that will own `netjes`, with two-factor authentication on.
2. For the first release, create a granular access token: "Read and write (publish and stage)" (the stage-only permission cannot run `npm publish`), all packages (`netjes` does not exist yet), "Bypass two-factor authentication" checked, and an expiry a few days out. Store it as the `NPM_TOKEN` secret of the `release` environment.
3. When cutting the release after that one, first open **netjes → Settings → Trusted publishing** and add GitHub Actions with owner `DennisVerhoeven`, repository `netjes`, workflow `release.yml` and environment `release`, and allow direct `npm publish` (new configurations allow only `npm stage publish` by default). A new configuration expires if nothing publishes through it within two days, so add it just before the release that uses it. npm then publishes through trusted publishing rather than the token. Afterwards, set the package's publishing access to "Require two-factor authentication and disallow tokens", revoke the token and delete the `NPM_TOKEN` secret.

The publish job installs npm 11, which trusted publishing needs (11.5.1 or later). Node.js 22 comes with npm 10.

## Cutting a release

1. On a branch off `main`:
   - set the version in `packages/netjes/package.json` with `npm version <version> --no-git-tag-version -w packages/netjes`, run `npm install --package-lock-only` to carry it into `package-lock.json`, and set the same `<Version>` in `Directory.Build.props`. `VersionTests` fails when the npm and NuGet versions differ;
   - set the same version where the docs pin it. `VERSION_PINS` in [scripts/release.mjs](scripts/release.mjs) lists every pinned text, such as the install commands in the getting-started page and the package READMEs, and the version in the README's "Status" paragraph. `node scripts/release.mjs check` reports a pin that carries another version, or one that has gone from its file, so when you reword a pinned text in a doc, change it in `VERSION_PINS` too. Then run `npm run docs:llms`, which carries them into `llms-full.txt`; `npm run docs:check` fails until it has run;
   - in `CHANGELOG.md`, move the entries under `## [Unreleased]` to a new `## [<version>] - <yyyy-mm-dd>` section and update the comparison links at the bottom;
   - `node scripts/release.mjs check v<version> --repository DennisVerhoeven/netjes` should report no problems.
2. Merge it, and wait for CI to pass on `main` for the merge commit.
3. Tag that commit and push the tag:
   ```bash
   git tag -a v<version> -m "Netjes <version>" <commit>
   git push origin v<version>
   ```
4. **Actions → Release → Run workflow**: run it from `main`, with tag `v<version>`. After the check, test and pack job passes, approve the `release` deployment.
5. Check the result: both packages on nuget.org (indexing takes a few minutes), `npm view netjes@<version>`, the GitHub release, and a new app made from the published packages:
   ```bash
   npx netjes@<version> new my-app
   cd my-app && npm test && npx netjes dev
   ```

## When something goes wrong

- **A check, test or pack step fails.** Nothing has been published. Fix it on `main`. Then delete the tag (locally and on GitHub) and tag the fixed commit with the same version.
- **The publish job fails halfway**, for example on an expired npm token. Fix the cause and re-run the failed jobs. What was published already is skipped.
- **A published release is broken.** A version cannot be published twice: npm never reuses one, and nuget.org can unlist a version but not delete it. Release a fixed patch version, then mark the broken one with `npm deprecate netjes@<version> "<reason>"` and unlist it on nuget.org.
