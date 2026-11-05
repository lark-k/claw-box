"""Apply the reviewable Quantum product overlay to the pinned upstream checkout."""
import base64
import hashlib
from pathlib import Path
import re
import subprocess
import io
import json

ROOT = Path(__file__).resolve().parents[2]
UPSTREAM = ROOT / 'src/Quantum.Harness.Upstream'
PIN = 'ce7073d51254c26cd63b22a60835b8f948deb447'
NAME = 'Quantum Harness'
APP_ID = 'com.quantum.harness.next'
changed = []
receipt_file = UPSTREAM / '.dsh-build/quantum-brand.json'
previous = json.loads(receipt_file.read_text()).get('files', {}) if receipt_file.exists() else {}
managed = dict(previous)


def original(relative):
    return subprocess.check_output(['git', '-C', str(UPSTREAM), 'show', f'{PIN}:{relative}'])


def publish(relative, expected, baseline=None):
    """Refuse unrelated edits rather than silently replacing developer changes."""
    target = UPSTREAM / relative
    current = target.read_bytes() if target.exists() else None
    managed[relative] = hashlib.sha256(expected).hexdigest()
    # Git checkout may have normalized line endings on Windows.
    normalized = lambda value: value.replace(b'\r\n', b'\n') if value is not None else None
    if normalized(current) == normalized(expected):
        return
    if (current is not None and normalized(current) != normalized(baseline)
            and hashlib.sha256(current).hexdigest() != previous.get(relative)):
        raise RuntimeError(f'Unrecognized local edit; left unchanged: {relative}')
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(expected)
    changed.append(relative)


def edit(relative, transform):
    baseline = original(relative)
    source = baseline.decode('utf-8').replace('\r\n', '\n')
    publish(relative, transform(source).encode(), baseline)


def product_text(text):
    return text.replace('DeepSeek Harness', NAME).replace('DSH Local Build', NAME).replace('DSH 本地构建', NAME)


def apply():
    """Brand presentation and native identity without modifying tools or permissions."""
    head = subprocess.check_output(['git', '-C', str(UPSTREAM), 'rev-parse', 'HEAD'], text=True).strip()
    if head != PIN:
        raise RuntimeError('Unexpected upstream revision; review the branding overlay before updating.')
    tracked = subprocess.check_output(['git', '-C', str(UPSTREAM), 'ls-files', '-z']).decode().split('\0')
    for relative in tracked:
        runtime = (relative.startswith('packages/client/') and '/src/' in relative
                   or relative.startswith('apps/desktop/src/') or relative.startswith('apps/desktop/renderer/'))
        composed = relative == 'apps/desktop/src/main.ts' or relative.startswith('packages/client/locale/src/locales/')
        if runtime and not composed and relative.endswith(('.ts', '.tsx', '.html', '.css')) and '/tests/' not in relative:
            text = (UPSTREAM / relative).read_text(encoding='utf-8')
            # Adopt an already-applied pure text overlay if an earlier receipt omitted it.
            existing_overlay = (relative not in previous and NAME in text
                                and product_text(original(relative).decode('utf-8').replace('\r\n', '\n')) == text)
            if product_text(text) != text or existing_overlay:
                edit(relative, product_text)

    # Localized fallback name owns the sidebar in the custom build profile.
    for relative in tracked:
        if relative.startswith('packages/client/locale/src/locales/') and relative.endswith('.ts'):
            edit(relative, lambda text: re.sub(r"('brand\.localBuild': )'[^']*'", r"\1'Quantum Harness'", product_text(text)))

    main = 'apps/desktop/src/main.ts'
    def native_identity(text):
        text = product_text(text).replace("import { installFileActions }", "import { QuantumCompanion } from './quantum-companion.ts'\nimport { installFileActions }", 1)
        text = text.replace('let focusPrimaryWindow =',
                            "app.setName('Quantum Harness')\nif (process.platform === 'win32') app.setAppUserModelId('com.quantum.harness.next')\n\nlet focusPrimaryWindow =", 1)
        text = text.replace('    width: 1280,', "    title: 'Quantum Harness',\n    icon: app.isPackaged ? join(process.resourcesPath, 'icon.png') : join(app.getAppPath(), 'resources', 'icon-windows.png'),\n    width: 1280,", 1)
        text = text.replace('stopAccount = accountBackend.watch(', "if (process.env.DSH_CLIENT_BUILD_PROFILE !== 'quantum') stopAccount = accountBackend.watch(", 1)
        text = text.replace('needsWelcome(', "quantumNeedsWelcome(")
        text = text.replace("let focusPrimaryWindow =", "const quantumNeedsWelcome = (value: Parameters<typeof needsWelcome>[0]) => process.env.DSH_CLIENT_BUILD_PROFILE !== 'quantum' && needsWelcome(value)\n\nlet focusPrimaryWindow =", 1)
        text = text.replace('  installDesktopDirectoryPicker(() => mainWindow)', '''  if (process.env.DSH_CLIENT_BUILD_PROFILE === 'quantum') {
    const quantumBox = new QuantumCompanion()
    ipcMain.handle('quantum-box:call', (event, request: unknown) => {
      assertDesktopSender(event, ['app'])
      if (event.sender !== mainWindow?.webContents || event.senderFrame !== event.sender.mainFrame) throw new Error('Rejected box command')
      return quantumBox.call(request)
    })
    app.on('will-quit', () => { quantumBox.dispose() })
  }
  installDesktopDirectoryPicker(() => mainWindow)''', 1)
        return text
    # Compose from the same baseline; the prior product-only pass is an accepted intermediate.
    edit(main, native_identity)
    publish('apps/desktop/src/quantum-companion.ts', (Path(__file__).parent / 'quantum-companion.ts').read_bytes())
    edit('apps/desktop/src/preload-app.ts', lambda text: text.replace(
        "  contextBridge.exposeInMainWorld('dshOnboarding', {",
        "  if (process.isMainFrame) contextBridge.exposeInMainWorld('quantumBox', { call: (request: unknown) => ipcRenderer.invoke('quantum-box:call', request) as Promise<unknown> })\n  contextBridge.exposeInMainWorld('dshOnboarding', {", 1))
    edit('apps/desktop/tsdown.config.ts', lambda text: text.replace(
        "const clientVersionDefine = {", "const clientVersionDefine = { 'process.env.DSH_CLIENT_BUILD_PROFILE': JSON.stringify(clientEnvironment.DSH_CLIENT_BUILD_PROFILE),"))
    publish('apps/desktop/src/runtime-path.ts', (Path(__file__).parent / 'runtime-path.ts').read_bytes())
    publish('apps/desktop/tests/runtime-path.spec.ts', (Path(__file__).parent / 'runtime-path.spec.ts').read_bytes())
    edit('apps/desktop/src/host-process.ts', lambda text: text
         .replace("import { desktopNodeEnvironment }", "import { HOST_BOOTSTRAP } from './runtime-path.ts'\nimport { desktopNodeEnvironment }", 1)
         .replace("    const entry = join(this.runtimeDir, 'node_modules', '@deepseek-ai', 'dsh-desktop-host', 'lib', 'index.js')", '')
         .replace('      entry,', "      '-e', HOST_BOOTSTRAP,", 1))

    # The existing logo component retains its exported API and original geometry constants.
    fish = 'packages/client/ui-primitives/src/FishLogo.tsx'
    def logo(text):
        return text.replace('  return (', "  if (process.env.DSH_CLIENT_BUILD_PROFILE === 'quantum') {\n    return <img src=\"/favicon.svg\" alt=\"\" aria-hidden=\"true\" className={className} width={size} height={size} />\n  }\n  return (", 1)
    edit(fish, logo)
    edit('packages/client/ui-sidebar/src/client/SidebarRoot.tsx', lambda text: text
         .replace('const buildVersion = localBuildVersion()', "const buildVersion = process.env.DSH_CLIENT_BUILD_PROFILE === 'quantum' ? undefined : localBuildVersion()")
         .replace('<FishLogo size={24} />', '<FishLogo size={28} />'))
    edit('packages/client/ui-sidebar/src/client/SidebarRoot.module.css', lambda text: text + '''
/* Quantum product lockup: one line, shared baseline, no build metadata. */
.brandIdentity { gap: 9px; height: 36px; }
.brandName { height: 36px; letter-spacing: -0.025em; }
.fallbackBrandName { font-family: "Segoe UI", sans-serif; font-size: 17px; font-weight: 650; letter-spacing: -0.025em; }
:global([data-windows-titlebar]) .brandName { font-weight: 650; }
.brandMark img { display: block; flex: none; object-fit: contain; }
''')
    png = (ROOT / 'src/ClawBox.Desktop/Assets/QuantumHarness.png').read_bytes()
    ico = (ROOT / 'src/ClawBox.Desktop/Assets/QuantumHarness.ico').read_bytes()
    css = ".mark { display: inline-block; flex-shrink: 0; background: url('./QuantumHarness.png') center / contain no-repeat; }\n"
    publish('packages/client/ui-primitives/src/QuantumLogo.module.css', css.encode())
    publish('packages/client/ui-primitives/src/QuantumHarness.png', png)
    hero = 'packages/client/ui-conversation/src/client/skeleton/EmptyHero.tsx'
    edit(hero, lambda text: text.replace('  FISH_LOGO_PATH,', '  FishLogo, FISH_LOGO_PATH,', 1)
         .replace('fallback: <HeroFish hovering={hovering} />,', "fallback: process.env.DSH_CLIENT_BUILD_PROFILE === 'quantum' ? <FishLogo size={38} /> : <HeroFish hovering={hovering} />,")
         .replace("<span className={css.previewBadge}>{t('hero.preview')}</span>", "{process.env.DSH_CLIENT_BUILD_PROFILE !== 'quantum' && <span className={css.previewBadge}>{t('hero.preview')}</span>}"))
    edit('packages/client/ui-conversation/src/client/locales.ts', lambda text: re.sub(r"(?m)^(\s*'hero\.headline': ).*$", r"\1'Quantum Harness',", text))
    edit('packages/client/ui-conversation/src/client/skeleton/HeroShell.module.css', lambda text: text + '''
/* Quantum home wordmark shares the sidebar type treatment. */
.headline { gap: 12px; }
.titleGroup { font-family: "Segoe UI", sans-serif; font-weight: 650; letter-spacing: -0.035em; }
''')
    for relative in ('apps/desktop/resources/icon-windows.png', 'apps/desktop/resources/icon.png'):
        publish(relative, png, original(relative))
    publish('apps/desktop/resources/tray-windows.ico', ico, original('apps/desktop/resources/tray-windows.ico'))
    for relative in ('apps/web/public/icon-192.png', 'apps/web/public/icon-512.png',
                     'apps/web/public/icon-maskable-512.png', 'apps/web/public/apple-touch-icon.png'):
        publish(relative, png, original(relative))
    svg = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><image width="256" height="256" href="data:image/png;base64,'
           + base64.b64encode(png).decode() + '"/></svg>\n').encode()
    for relative in ('apps/web/public/favicon.svg', 'apps/web/public/favicon-dark.svg'):
        publish(relative, svg, original(relative))
    for relative in ('apps/desktop/resources/icon-windows.svg', 'apps/desktop/resources/icon.svg',
                     'apps/desktop/renderer/assets/welcome-brand.svg'):
        publish(relative, svg, original(relative))
    for relative in tracked:
        if relative.startswith('apps/desktop/installer/locales/') and relative.endswith('.json'):
            edit(relative, product_text)
    from PIL import Image, ImageDraw, ImageFont
    for relative in tracked:
        if relative.startswith('apps/desktop/installer/assets/') and relative.endswith('.png'):
            baseline = original(relative)
            width, height = Image.open(io.BytesIO(baseline)).size
            dark = 'dark' in relative
            canvas = Image.new('RGBA', (width, height), (21,21,23,255) if dark else (255,255,255,255))
            mark = Image.open(io.BytesIO(png)).convert('RGBA')
            vertical = height > width
            size = min(width//2, height//3) if vertical else int(height*.8)
            mark.thumbnail((size, size), Image.Resampling.LANCZOS)
            x, y = ((width-mark.width)//2, height//5) if vertical else (4, (height-mark.height)//2)
            canvas.alpha_composite(mark, (x, y))
            draw = ImageDraw.Draw(canvas)
            label = 'Quantum\nHarness' if vertical else NAME
            font_size = max(9, int(width*.14) if vertical else int(height*.47))
            font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', font_size)
            while draw.textbbox((0,0), label, font=font)[2] > (width-8 if vertical else width-size-12) and font_size > 8:
                font_size -= 1
                font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', font_size)
            draw.text((8 if vertical else size+10, y+size+12 if vertical else (height-font_size)//2-3), label, font=font,
                      fill='white' if dark else '#172033')
            output = io.BytesIO()
            canvas.save(output, format='PNG')
            publish(relative, output.getvalue(), baseline)
    config = 'apps/desktop/scripts/electron-builder-config.mjs'
    publish('apps/desktop/scripts/quantum-nsis.mjs', (Path(__file__).parent / 'quantum-nsis.mjs').read_bytes())
    edit(config, lambda text: product_text(text).replace('`deepseek-harness-', '`quantum-harness-')
         .replace("import { installWindowsDirectoryInstaller }", "import { installQuantumNsis } from './quantum-nsis.mjs'\nimport { installWindowsDirectoryInstaller }", 1)
         .replace("if (resolvedPlatform === 'win32') installWindowsDirectoryInstaller()", "if (resolvedPlatform === 'win32' && env.QUANTUM_STANDARD_NSIS === '1') installQuantumNsis()\n  if (resolvedPlatform === 'win32') installWindowsDirectoryInstaller()")
         .replace("const policy = packagesMacOS && unsigned ? undefined", "const policy = unsigned ? undefined")
         .replace("if (resolvedPlatform === 'win32') installWindowsDirectoryInstaller()", "if (resolvedPlatform === 'win32' && env.QUANTUM_STANDARD_NSIS !== '1') installWindowsDirectoryInstaller()")
         .replace("    extraMetadata: {", "    extraMetadata: {\n      name: 'quantum-harness', author: { name: 'Quantum Harness' },", 1)
         .replace('    beforeBuild: async () => {', "    beforeBuild: async () => {\n      if (env.QUANTUM_STANDARD_NSIS === '1') return true;", 1)
         .replace("installerSidebar: join(buildPaths.root, 'installer-ui', 'uninstaller-sidebar.bmp')", "installerSidebar: env.QUANTUM_STANDARD_NSIS === '1' ? undefined : join(buildPaths.root, 'installer-ui', 'uninstaller-sidebar.bmp')")
         .replace("uninstallerSidebar: join(buildPaths.root, 'installer-ui', 'uninstaller-sidebar.bmp')", "uninstallerSidebar: env.QUANTUM_STANDARD_NSIS === '1' ? undefined : join(buildPaths.root, 'installer-ui', 'uninstaller-sidebar.bmp')")
         .replace("include: fileURLToPath(new URL('./installer.nsh', import.meta.url))", "include: env.QUANTUM_STANDARD_NSIS === '1' ? undefined : fileURLToPath(new URL('./installer.nsh', import.meta.url))")
         .replace('    extraResources: [', "    extraResources: [" + chr(10) + "      ...(packagesWindows && env.QUANTUM_BOX_COMPANION_DIR ? [{ from: env.QUANTUM_BOX_COMPANION_DIR, to: 'quantum-box' }] : []),", 1)
         .replace('allowToChangeInstallationDirectory: false', 'allowToChangeInstallationDirectory: true'))
    edit('apps/desktop/scripts/desktop-toolchain-preflight.ts', lambda text: text.replace(
        "  if (platform === 'win32') failures.push", "  if (platform === 'win32' && environment.QUANTUM_STANDARD_NSIS !== '1') failures.push"))
    edit('apps/desktop/scripts/prepare-runtime.ts', lambda text: text.replace('chmodSync, cpSync, mkdirSync, rmSync, writeFileSync', 'chmodSync, cpSync, mkdirSync, readFileSync, rmSync, writeFileSync')
         .replace("  const archive = await packagingStep(process.env.DSH_DESKTOP_PACKAGING_RUN_DIR, 'download:electron',", "  if (process.env.QUANTUM_REUSE_ELECTRON === '1' && platform === 'win32' && arch === 'x64') {\n    const exe = join(BUILD_PATHS.electron, 'electron.exe')\n    const actual = execFileSync(exe, ['-p', 'process.versions.electron'], { encoding: 'utf8', env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' } }).trim()\n    if (actual !== version || readFileSync(join(BUILD_PATHS.electron, 'version'), 'utf8').trim() !== version) throw new Error('Cached Electron version does not match')\n    return execFileSync(exe, ['-p', 'process.versions.node'], { encoding: 'utf8', env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' } }).trim()\n  }\n  const archive = await packagingStep(process.env.DSH_DESKTOP_PACKAGING_RUN_DIR, 'download:electron',", 1)
         .replace("cacheRoot: BUILD_PATHS.downloads }", "cacheRoot: BUILD_PATHS.downloads, ...(process.env.QUANTUM_ELECTRON_CHECKSUM_FILE ? { checksums: Object.fromEntries(readFileSync(process.env.QUANTUM_ELECTRON_CHECKSUM_FILE, 'utf8').trim().split(/\\r?\\n/u).map(line => { const [hash, filename] = line.trim().split(/\\s+/u); return [filename!.replace(/^\\*/u, ''), hash!]; })) } : {}) }"))
    edit('apps/desktop/scripts/prepare-primary-runtime.ts', lambda text: text
         .replace("import { readFileSync } from 'node:fs'", "import { cpSync, readFileSync } from 'node:fs'\nimport lock from '../../../scripts/primary-runtime/lock.json' with { type: 'json' }")
         .replace('preparePrimaryRuntime as preparePayload, smokePrimaryRuntime as smokePayload', 'preparePrimaryRuntime as preparePayload, smokePrimaryRuntime as smokePayload, primaryRuntimePayloadDigest')
         .replace('  await preparePayload({ target: resolveDesktopBuildTarget(), output: paths.runtime, cache: paths.downloads, version })', '''  const reuse = process.env.QUANTUM_REUSE_RUNTIME
  if (reuse) {
    const manifest = parsePrimaryRuntime(JSON.parse(readFileSync(join(reuse, 'primary-runtime', 'runtime.json'), 'utf8')))
    const target = resolveDesktopBuildTarget()
    if (manifest.desktopVersion !== version || manifest.payloadDigest !== primaryRuntimePayloadDigest(target, lock, manifest.pnpm)) throw new Error('Quantum cached runtime does not match the pinned lock')
    cpSync(join(reuse, 'primary-runtime'), join(paths.runtime, 'primary-runtime'), { recursive: true })
    cpSync(join(reuse, 'office-skills'), join(paths.runtime, 'office-skills'), { recursive: true })
  } else await preparePayload({ target: resolveDesktopBuildTarget(), output: paths.runtime, cache: paths.downloads, version })'''))
    # Upstream's unsigned local build profile is the coherent Quantum artifact profile.
    edit('scripts/client-build-environment.ts', lambda text: text.replace("DSH_CLIENT_BUILD_PROFILE: 'local',", "DSH_CLIENT_BUILD_PROFILE: 'quantum',\n        DSH_CLIENT_TITLE: 'Quantum Harness',"))
    edit('apps/desktop/scripts/desktop-package-environment.mjs', lambda text: text
         .replace("  const compression = environment.PACKAGE_WINDOWS_COMPRESSION_LEVEL", "  if (options.unsigned && platform === 'win32') return { ...environment, DSH_DESKTOP_APP_ID: 'com.quantum.harness.next', DSH_DESKTOP_UNSIGNED: '1' }\n  const compression = environment.PACKAGE_WINDOWS_COMPRESSION_LEVEL", 1)
         .replace("if (target.platform !== 'darwin' || !options.unsigned) resolveDesktopPolicyEnvironment(environment)", "if (!options.unsigned) resolveDesktopPolicyEnvironment(environment)"))
    edit('apps/web/public/manifest.webmanifest', lambda text: text.replace('DeepSeek Harness', NAME).replace('"DSH"', '"Quantum Harness"'))
    edit('apps/desktop/scripts/smoke-packaged-runtime.ts', lambda text: product_text(text)
         .replace("import { join } from 'node:path'", "import { join } from 'node:path'\nimport { cpSync, mkdirSync } from 'node:fs'")
         .replace('const application = windows ?', 'const application = process.env.QUANTUM_SMOKE_APPLICATION_DIRECTORY ?? (windows ?')
         .replace("'Quantum Harness.app', 'Contents')", "'Quantum Harness.app', 'Contents'))")
         .replace('const resources = join(application,', "if (windows && process.env.QUANTUM_STAGE_SMOKE_APPLICATION === '1' && process.env.QUANTUM_SMOKE_APPLICATION_DIRECTORY) {\n  mkdirSync(application, { recursive: true })\n  cpSync(join(artifacts, 'win-unpacked'), application, { recursive: true })\n}\nconst resources = join(application,"))
    edit('apps/desktop/scripts/smoke-runtime.ts', lambda text: text
        .replace('DSH_HOME: home, HOME: home, USERPROFILE: home,', 'DSH_CLIENT_VERSION: runtime.release.version, DSH_HOME: home, HOME: home, USERPROFILE: home,')
        .replace("throw new Error('desktop runtime: external plugin loaded another Cordis instance')", "throw new Error('desktop runtime: external plugin loaded another Cordis instance: ' + import.meta.resolve('@deepseek-ai/cordis') + '; constructor=' + ctx.constructor.name + '; same=' + (ctx.constructor === Context))")
        .replace(
        "if (await pluginResponse.text() !== 'plugin route ready') throw new Error('desktop runtime: plugin HTTP route failed')",
        "const pluginBody = await pluginResponse.text()\n    if (pluginBody !== 'plugin route ready') throw new Error(`desktop runtime: plugin HTTP route failed (${pluginResponse.status}): ${pluginBody.slice(0, 250)}; ${String(Reflect.get(host, 'stderr')).slice(-6000)}`)"))
    edit('packages/client/ui-sidebar/tests/sidebar-root.client.spec.tsx', product_text)
    # Quantum replaces the official account composition, not just its menu label.
    account = 'packages/client/ui-settings-account/'
    for source, destination in (
        ('box-settings.ts', 'src/box-settings.ts'),
        ('box-locales.ts', 'src/client/box-locales.ts'),
        ('QuantumBox.tsx', 'src/client/QuantumBox.tsx'),
        ('QuantumBox.module.css', 'src/client/QuantumBox.module.css'),
        ('quantum-box-apply.ts', 'src/client/quantum-box-apply.ts'),
        ('quantum-box.client.spec.tsx', 'tests/quantum-box.client.spec.tsx'),
    ):
        publish(account + destination, (Path(__file__).parent / source).read_bytes())
    edit(account + 'src/client/index.ts', lambda text: product_text(text)
         .replace("import type { TranscriptViewMode }", "import { applyQuantumBox } from './quantum-box-apply.ts'\nimport type { TranscriptViewMode }", 1)
         .replace("export const inject = ['slots',", "export const inject = process.env.DSH_CLIENT_BUILD_PROFILE === 'quantum' ? ['slots', 'locale', 'configForms', 'remote', 'remote.credentials', 'remote.settings'] : ['slots',", 1)
         .replace('export function apply(ctx: Context): void {', "export function apply(ctx: Context): void {\n  if (process.env.DSH_CLIENT_BUILD_PROFILE === 'quantum') { applyQuantumBox(ctx); return }", 1))
    edit(account + 'src/index.ts', lambda text: text
         .replace("import z from '@deepseek-ai/schemastery'", "import z from '@deepseek-ai/schemastery'\nimport { BoxSettingsFields } from './box-settings.ts'")
         .replace('export interface Config extends ContactConfig {', 'export interface Config extends ContactConfig {\n  /** Pending device address; no credentials or connection state. */\n  boxHost?: Volatile<string | undefined>\n  /** Pending device SSH port. */\n  boxSshPort?: Volatile<number | undefined>')
         .replace('export const Config = z.object({', 'export const Config = z.object({\n  boxHost: BoxSettingsFields.boxHost.volatile(),\n  boxSshPort: BoxSettingsFields.boxSshPort.volatile(),'))
    edit('packages/client/ui-settings-models/src/client/index.ts', lambda text: text
         .replace("if (!('dshDesktop' in globalThis)) ctx.slots.inject('settings.onboarding'", "if (process.env.DSH_CLIENT_BUILD_PROFILE !== 'quantum' && !('dshDesktop' in globalThis)) ctx.slots.inject('settings.onboarding'")
         .replace("  ctx.slots.inject('settings.onboarding', () => ctx.slots.register({", "  if (process.env.DSH_CLIENT_BUILD_PROFILE !== 'quantum') ctx.slots.inject('settings.onboarding', () => ctx.slots.register({"))
    receipt = UPSTREAM / '.dsh-build/quantum-brand.json'
    receipt.parent.mkdir(parents=True, exist_ok=True)
    receipt.write_text(json.dumps(dict(name=NAME, appId=APP_ID, upstream=PIN,
                      pngSha256=hashlib.sha256(png).hexdigest(), icoSha256=hashlib.sha256(ico).hexdigest(),files=managed), indent=2)+'\n')
    print(f'Quantum branding applied: {len(changed)} files updated.')


if __name__ == '__main__':
    apply()
