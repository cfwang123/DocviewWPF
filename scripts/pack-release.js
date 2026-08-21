#!/usr/bin/env node
/**
 * 将 DocviewWPF Release 输出打成 7z，并可选发布到 GitHub Releases：
 *   release/DocviewWPF_x.x.x.7z
 *
 * 用法：
 *   node scripts/pack-release.js                    # 打包已有 Release 输出
 *   node scripts/pack-release.js --build            # 先 slx 编译再打包
 *   node scripts/pack-release.js --publish          # 打包后 gh release create
 *   node scripts/pack-release.js --build --publish  # 编译 + 打包 + 发布
 *   node scripts/pack-release.js --publish-only     # 仅发布已有 7z（不重新打包）
 *   node scripts/pack-release.js --version 1.2.3
 */
"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const ROOT = path.resolve(__dirname, "..");
const CSPROJ = path.join(ROOT, "DocviewWPF", "DocviewWPF.csproj");
const CHANGELOG = path.join(ROOT, "CHANGELOG.md");
const OUT_DIR = path.join(ROOT, "DocviewWPF", "bin", "Release", "net48");
const RELEASE_DIR = path.join(ROOT, "release");

main();

function main() {
	const args = parseargs(process.argv.slice(2));
	const ver = args.version || readversion(CSPROJ);
	if (!ver) {
		console.error("无法解析版本号（csproj <Version> 或 --version）");
		process.exit(1);
	}

	const zipName = `DocviewWPF_${ver}.7z`;
	const zipPath = path.join(RELEASE_DIR, zipName);

	if (args.publishOnly) {
		if (!fs.existsSync(zipPath)) {
			console.error("未找到发布包:", zipPath);
			console.error("请先打包，或去掉 --publish-only");
			process.exit(1);
		}
		publishrelease(ver, zipPath, args);
		return;
	}

	if (args.build) {
		console.log("编译 Release…");
		const r = spawnSync("slx", ["DocviewWPF"], {
			cwd: ROOT,
			stdio: "inherit",
			shell: true,
		});
		if (r.status !== 0) {
			console.error("编译失败");
			process.exit(r.status || 1);
		}
	}

	const exe = path.join(OUT_DIR, "DocviewWPF.exe");
	if (!fs.existsSync(exe)) {
		console.error("未找到 Release 输出:", exe);
		console.error("请先执行: slx DocviewWPF  或  node scripts/pack-release.js --build");
		process.exit(1);
	}

	fs.mkdirSync(RELEASE_DIR, { recursive: true });
	if (fs.existsSync(zipPath)) fs.unlinkSync(zipPath);

	const seven = find7z();
	if (!seven) {
		console.error("未找到 7z（请安装 7-Zip 或将 7z 加入 PATH）");
		process.exit(1);
	}

	console.log("7z:", seven.path);
	console.log("源目录:", OUT_DIR);
	console.log("输出:", zipPath);

	// 在输出目录内打包 *，排除调试/日志
	const packArgs = [
		"a",
		"-t7z",
		"-mx=9",
		"-mmt=on",
		"-xr!*.pdb",
		"-xr!*.xml",
		"-xr!logs",
		"-xr!*.log",
		zipPath,
		"*",
	];
	const r = spawnSync(seven.path, packArgs, {
		cwd: OUT_DIR,
		stdio: "inherit",
		shell: seven.shell,
	});
	if (r.status !== 0) {
		console.error("7z 打包失败, code=", r.status, r.error || "");
		process.exit(r.status || 1);
	}

	const st = fs.statSync(zipPath);
	const mb = (st.size / (1024 * 1024)).toFixed(2);
	console.log(`完成: ${zipPath} (${mb} MB)`);

	if (args.publish) publishrelease(ver, zipPath, args);
}

function parseargs(argv) {
	const o = {
		build: false,
		publish: false,
		publishOnly: false,
		draft: false,
		prerelease: false,
		version: null,
		notesfile: null,
	};
	for (let i = 0; i < argv.length; i++) {
		const a = argv[i];
		if (a === "--build" || a === "-b") o.build = true;
		else if (a === "--publish" || a === "-p") o.publish = true;
		else if (a === "--publish-only") o.publishOnly = true;
		else if (a === "--draft") o.draft = true;
		else if (a === "--prerelease") o.prerelease = true;
		else if (a === "--version" || a === "-v") {
			o.version = argv[++i] || null;
		} else if (a === "--notes-file") {
			o.notesfile = argv[++i] || null;
		} else if (a === "--help" || a === "-h") {
			console.log(
				"用法: node scripts/pack-release.js [--build] [--publish] [--publish-only] [--version x.y.z] [--notes-file path] [--draft] [--prerelease]"
			);
			process.exit(0);
		}
	}
	return o;
}

function readversion(csproj) {
	const text = fs.readFileSync(csproj, "utf8");
	const m = text.match(/<Version>\s*([^<\s]+)\s*<\/Version>/);
	return m ? m[1].trim() : null;
}

function find7z() {
	const pf = process.env["ProgramFiles"] || "C:\\Program Files";
	const pf86 = process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)";
	const exePaths = [
		process.env.SEVEN_ZIP,
		path.join(pf, "7-Zip", "7z.exe"),
		path.join(pf86, "7-Zip", "7z.exe"),
		"C:\\bin\\7z.exe",
	].filter(Boolean);

	for (const p of exePaths) {
		if (fs.existsSync(p)) return { path: p, shell: false };
	}

	// PATH 上的 7z / 7z.cmd：需 shell
	for (const name of ["7z", "7za"]) {
		const r = spawnSync(name, [], { encoding: "utf8", shell: true });
		if (!r.error) return { path: name, shell: true };
	}
	return null;
}

function findgh() {
	const pf = process.env["ProgramFiles"] || "C:\\Program Files";
	const exe = path.join(pf, "GitHub CLI", "gh.exe");
	if (fs.existsSync(exe)) return { path: exe, shell: false };

	const r = spawnSync("gh", ["--version"], { encoding: "utf8", shell: true });
	if (!r.error && r.status === 0) return { path: "gh", shell: true };
	return null;
}

function extractchangelog(ver) {
	if (!fs.existsSync(CHANGELOG)) return null;
	const lines = fs.readFileSync(CHANGELOG, "utf8").split(/\r?\n/);
	let start = -1;
	for (let i = 0; i < lines.length; i++) {
		if (lines[i].startsWith(`## [${ver}]`)) {
			start = i + 1;
			break;
		}
	}
	if (start < 0) return null;

	const out = [];
	for (let i = start; i < lines.length; i++) {
		if (/^## \[\d/.test(lines[i])) break;
		out.push(lines[i]);
	}
	const notes = out.join("\n").trim();
	return notes || null;
}

function publishrelease(ver, zipPath, args) {
	const gh = findgh();
	if (!gh) {
		console.error("未找到 gh（GitHub CLI），请先安装: https://cli.github.com/");
		process.exit(1);
	}

	const auth = spawnSync(gh.path, ["auth", "status"], {
		cwd: ROOT,
		encoding: "utf8",
		shell: gh.shell,
	});
	if (auth.status !== 0) {
		console.error("gh 未登录，请先执行: gh auth login");
		process.exit(1);
	}

	const tag = ver.startsWith("v") ? ver : `v${ver}`;
	let notes = null;
	if (args.notesfile) {
		if (!fs.existsSync(args.notesfile)) {
			console.error("未找到 notes 文件:", args.notesfile);
			process.exit(1);
		}
		notes = fs.readFileSync(args.notesfile, "utf8").trim();
	} else {
		notes = extractchangelog(ver);
	}
	if (!notes) {
		console.error(`CHANGELOG.md 中未找到 [${ver}] 条目（可用 --notes-file 指定）`);
		process.exit(1);
	}

	fs.mkdirSync(RELEASE_DIR, { recursive: true });
	const notesPath = path.join(RELEASE_DIR, `.notes_${ver}.md`);
	fs.writeFileSync(notesPath, notes, "utf8");

	const ghArgs = [
		"release",
		"create",
		tag,
		zipPath,
		"--title",
		tag,
		"--notes-file",
		notesPath,
	];
	if (args.draft) ghArgs.push("--draft");
	if (args.prerelease) ghArgs.push("--prerelease");

	console.log("发布:", tag);
	console.log("附件:", zipPath);
	console.log("说明:", args.notesfile || `CHANGELOG [${ver}]`);

	const r = spawnSync(gh.path, ghArgs, {
		cwd: ROOT,
		stdio: "inherit",
		shell: gh.shell,
	});
	try {
		fs.unlinkSync(notesPath);
	} catch (_) {}

	if (r.status !== 0) {
		console.error("gh release 失败, code=", r.status, r.error || "");
		process.exit(r.status || 1);
	}
	console.log("GitHub Release 已创建:", tag);
}
