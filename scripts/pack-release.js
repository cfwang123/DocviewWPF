#!/usr/bin/env node
/**
 * 将 DocviewWPF Release 输出打成 7z：
 *   release/DocviewWPF_x.x.x.7z
 *
 * 用法：
 *   node scripts/pack-release.js          # 打包已有 Release 输出
 *   node scripts/pack-release.js --build  # 先 slx 编译再打包
 *   node scripts/pack-release.js --version 1.2.3
 */
"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const ROOT = path.resolve(__dirname, "..");
const CSPROJ = path.join(ROOT, "DocviewWPF", "DocviewWPF.csproj");
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
	const zipName = `DocviewWPF_${ver}.7z`;
	const zipPath = path.join(RELEASE_DIR, zipName);
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
}

function parseargs(argv) {
	const o = { build: false, version: null };
	for (let i = 0; i < argv.length; i++) {
		const a = argv[i];
		if (a === "--build" || a === "-b") o.build = true;
		else if (a === "--version" || a === "-v") {
			o.version = argv[++i] || null;
		} else if (a === "--help" || a === "-h") {
			console.log(`用法: node scripts/pack-release.js [--build] [--version x.y.z]`);
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
