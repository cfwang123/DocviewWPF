using System;
using System.Collections.Generic;
using System.Globalization;

namespace DocviewWPF;

/// <summary>
/// 界面语言：zh / en / ja / ko。缺键时回退 en → 键名。
/// </summary>
static class Loc {
	public const string Zh = "zh";
	public const string En = "en";
	public const string Ja = "ja";
	public const string Ko = "ko";

	static string lang = Zh;
	static readonly Dictionary<string, Dictionary<string, string>> Table = Build();

	public static string Lang => lang;

	public static event Action LanguageChanged;

	public static readonly (string Code, string NativeName)[] Languages = {
		(Zh, "中文"),
		(En, "English"),
		(Ja, "日本語"),
		(Ko, "한국어"),
	};

	public static void Init(string code) {
		SetLanguage(code, fire: false);
	}

	public static void SetLanguage(string code, bool fire = true) {
		code = normalize(code);
		if (lang == code) return;
		lang = code;
		if (fire) LanguageChanged?.Invoke();
	}

	public static string T(string key) {
		if (string.IsNullOrEmpty(key)) return "";
		if (Table.TryGetValue(lang, out var d) && d.TryGetValue(key, out var s) && s != null)
			return s;
		if (lang != En && Table.TryGetValue(En, out var en) && en.TryGetValue(key, out var e) && e != null)
			return e;
		if (Table.TryGetValue(Zh, out var zh) && zh.TryGetValue(key, out var z) && z != null)
			return z;
		return key;
	}

	public static string Tf(string key, params object[] args) {
		try { return string.Format(CultureInfo.InvariantCulture, T(key), args); }
		catch { return T(key); }
	}

	static string normalize(string code) {
		if (string.IsNullOrWhiteSpace(code)) return detectSystem();
		code = code.Trim().ToLowerInvariant();
		if (code.StartsWith("zh")) return Zh;
		if (code.StartsWith("en")) return En;
		if (code.StartsWith("ja") || code.StartsWith("jp")) return Ja;
		if (code.StartsWith("ko") || code.StartsWith("kr")) return Ko;
		return detectSystem();
	}

	public static string detectSystem() {
		try {
			var c = CultureInfo.CurrentUICulture?.Name ?? "";
			return normalize(c);
		} catch {
			return Zh;
		}
	}

	static Dictionary<string, Dictionary<string, string>> Build() {
		var zh = new Dictionary<string, string>(StringComparer.Ordinal);
		var en = new Dictionary<string, string>(StringComparer.Ordinal);
		var ja = new Dictionary<string, string>(StringComparer.Ordinal);
		var ko = new Dictionary<string, string>(StringComparer.Ordinal);

		void a(string k, string z, string e, string j, string r) {
			zh[k] = z; en[k] = e; ja[k] = j; ko[k] = r;
		}

		// —— 菜单 ——
		a("menu", "菜单", "Menu", "メニュー", "메뉴");
		a("file", "文件", "File", "ファイル", "파일");
		a("open", "打开(_O)...", "Open(_O)...", "開く(_O)...", "열기(_O)...");
		a("recent", "最近文件(_R)", "Recent files(_R)", "最近使ったファイル(_R)", "최근 파일(_R)");
		a("print", "打印(_P)...", "Print(_P)...", "印刷(_P)...", "인쇄(_P)...");
		a("copy_path", "复制文件路径", "Copy file path", "パスをコピー", "경로 복사");
		a("show_in_explorer", "在资源管理器中显示", "Show in Explorer", "エクスプローラーで表示", "탐색기에서 표시");
		a("close", "关闭(_C)", "Close(_C)", "閉じる(_C)", "닫기(_C)");
		a("close_all", "关闭全部", "Close all", "すべて閉じる", "모두 닫기");
		a("exit", "退出(_X)", "Exit(_X)", "終了(_X)", "종료(_X)");
		a("view", "查看", "View", "表示", "보기");
		a("zoom_in", "放大", "Zoom in", "拡大", "확대");
		a("zoom_out", "缩小", "Zoom out", "縮小", "축소");
		a("zoom_100", "实际大小 100%", "Actual size 100%", "実際のサイズ 100%", "실제 크기 100%");
		a("fit_page", "适合页面", "Fit page", "ページ全体", "페이지 맞춤");
		a("fit_width", "适合宽度", "Fit width", "幅に合わせる", "너비 맞춤");
		a("prev_page", "上一页", "Previous page", "前のページ", "이전 페이지");
		a("next_page", "下一页", "Next page", "次のページ", "다음 페이지");
		a("goto_page", "跳到页...", "Go to page...", "ページへ移動...", "페이지 이동...");
		a("side_panel", "目录侧栏", "Outline panel", "目次パネル", "목차 패널");
		a("tools", "工具", "Tools", "ツール", "도구");
		a("pdf_pro_edit", "PDF 专业编辑(_E)...", "PDF Pro Editor(_E)...", "PDF プロ編集(_E)...", "PDF 전문 편집(_E)...");
		a("settings", "系统参数(_P)...", "Settings(_P)...", "設定(_P)...", "설정(_P)...");
		a("help", "帮助", "Help", "ヘルプ", "도움말");
		a("about", "关于 DocviewWPF", "About DocviewWPF", "DocviewWPF について", "DocviewWPF 정보");
		a("language", "语言", "Language", "言語", "언어");

		// —— 窗口按钮 ——
		a("minimize", "最小化", "Minimize", "最小化", "최소화");
		a("maximize", "最大化", "Maximize", "最大化", "최대화");
		a("restore", "还原", "Restore", "元に戻す", "이전 크기로");
		a("close_window", "关闭", "Close", "閉じる", "닫기");

		// —— 工具栏 ——
		a("tip_open", "打开 (Ctrl+O)", "Open (Ctrl+O)", "開く (Ctrl+O)", "열기 (Ctrl+O)");
		a("tip_print", "打印 (Ctrl+P)", "Print (Ctrl+P)", "印刷 (Ctrl+P)", "인쇄 (Ctrl+P)");
		a("page_label", "页码:", "Page:", "ページ:", "페이지:");
		a("tip_page", "页码 (Ctrl+G)", "Page (Ctrl+G)", "ページ (Ctrl+G)", "페이지 (Ctrl+G)");
		a("tip_prev", "上一页 (PgUp)", "Previous page (PgUp)", "前のページ (PgUp)", "이전 페이지 (PgUp)");
		a("tip_next", "下一页 (PgDn)", "Next page (PgDn)", "次のページ (PgDn)", "다음 페이지 (PgDn)");
		a("tip_fit_page", "适合单页 (Ctrl+0)", "Fit single page (Ctrl+0)", "1ページ表示 (Ctrl+0)", "한 페이지 맞춤 (Ctrl+0)");
		a("tip_fit_width", "适合宽度并连续显示 (Ctrl+2)", "Fit width continuous (Ctrl+2)", "幅に合わせて連続表示 (Ctrl+2)", "너비 맞춤 연속 (Ctrl+2)");
		a("tip_rotate_left", "向左旋转 90° ([)", "Rotate left 90° ([)", "左に90°回転 ([)", "왼쪽으로 90° 회전 ([)");
		a("tip_rotate_right", "向右旋转 90° (])", "Rotate right 90° (])", "右に90°回転 (])", "오른쪽으로 90° 회전 (])");
		a("tip_zoom_out", "缩小 (Ctrl+-)", "Zoom out (Ctrl+-)", "縮小 (Ctrl+-)", "축소 (Ctrl+-)");
		a("tip_zoom_in", "放大 (Ctrl++)", "Zoom in (Ctrl++)", "拡大 (Ctrl++)", "확대 (Ctrl++)");
		a("find_label", "查找:", "Find:", "検索:", "찾기:");
		a("tip_find", "查找 (Ctrl+F /)", "Find (Ctrl+F /)", "検索 (Ctrl+F /)", "찾기 (Ctrl+F /)");
		a("tip_find_prev", "上一个", "Previous", "前へ", "이전");
		a("tip_find_next", "下一个", "Next", "次へ", "다음");
		a("tip_case", "区分大小写", "Match case", "大文字と小文字を区別", "대/소문자 구분");
		a("tip_case_on", "区分大小写（已开）", "Match case (on)", "大文字と小文字を区別（オン）", "대/소문자 구분(켜짐)");
		a("tip_case_off", "忽略大小写（点击开启区分）", "Ignore case (click to match)", "大文字小文字を無視（クリックで区別）", "대/소문자 무시(클릭하여 구분)");
		a("tip_xlsx_edit", "编辑表格", "Edit spreadsheet", "表を編集", "표 편집");
		a("tip_xlsx_edit_exit", "退出编辑模式", "Exit edit mode", "編集モード終了", "편집 모드 종료");
		a("tip_save", "保存 (Ctrl+S)", "Save (Ctrl+S)", "保存 (Ctrl+S)", "저장 (Ctrl+S)");
		a("tip_pdf_edit", "打开 PDF 专业编辑窗口", "Open PDF Pro Editor", "PDF プロ編集を開く", "PDF 전문 편집 열기");
		a("tip_pdf_edit_short", "编辑 PDF", "Edit PDF", "PDF を編集", "PDF 편집");
		a("tip_pdf_save", "保存 PDF (Ctrl+S)", "Save PDF (Ctrl+S)", "PDF を保存 (Ctrl+S)", "PDF 저장 (Ctrl+S)");

		// —— PDF 嵌入编辑栏 ——
		a("tip_pdf_sel", "选择/移动", "Select / move", "選択/移動", "선택/이동");
		a("tip_pdf_text", "添加文字（再点页面）", "Add text (click page)", "テキスト追加（ページをクリック）", "텍스트 추가(페이지 클릭)");
		a("tip_pdf_img", "插入图片", "Insert image", "画像を挿入", "이미지 삽입");
		a("tip_pdf_edit_sel", "编辑选中原文（覆盖为可改文字）", "Edit selected text", "選択テキストを編集", "선택 텍스트 편집");
		a("tip_pdf_del", "删除选中对象", "Delete selection", "選択を削除", "선택 삭제");
		a("tip_font", "字体", "Font", "フォント", "글꼴");
		a("tip_font_size", "字号", "Size", "サイズ", "크기");
		a("tip_bold", "粗体", "Bold", "太字", "굵게");
		a("tip_italic", "斜体", "Italic", "斜体", "기울임");
		a("tip_fore", "文字颜色", "Text color", "文字色", "글자 색");
		a("tip_back", "文字背景色", "Text background", "文字の背景色", "글자 배경색");

		// —— XLSX 编辑栏 ——
		a("tip_merge", "合并选中单元格", "Merge cells", "セル結合", "셀 병합");
		a("tip_unmerge", "取消合并", "Unmerge", "結合解除", "병합 해제");
		a("tip_align_l", "水平左对齐", "Align left", "左揃え", "왼쪽 맞춤");
		a("tip_align_c", "水平居中", "Align center", "中央揃え", "가운데 맞춤");
		a("tip_align_r", "水平右对齐", "Align right", "右揃え", "오른쪽 맞춤");
		a("tip_valign_t", "垂直顶端对齐", "Align top", "上揃え", "위쪽 맞춤");
		a("tip_valign_m", "垂直居中对齐", "Align middle", "上下中央", "세로 가운데");
		a("tip_valign_b", "垂直底端对齐", "Align bottom", "下揃え", "아래쪽 맞춤");
		a("tip_cell_back", "单元格背景色", "Cell fill", "セルの塗りつぶし", "셀 배경색");
		a("tip_wrap", "单元格自动换行", "Wrap text", "折り返し", "텍스트 줄 바꿈");

		// —— 侧栏 / 状态 ——
		a("outline", "目录", "Outline", "目次", "목차");
		a("filter_outline", "筛选目录…", "Filter outline…", "目次を絞り込み…", "목차 필터…");
		a("ready", "就绪", "Ready", "準備完了", "준비됨");
		a("loading", "加载中… {0}", "Loading… {0}", "読み込み中… {0}", "로드 중… {0}");
		a("open_failed", "打开失败", "Open failed", "開けませんでした", "열기 실패");
		a("saved", "已保存: {0}", "Saved: {0}", "保存しました: {0}", "저장됨: {0}");
		a("no_changes", "无修改需要保存", "Nothing to save", "保存する変更はありません", "저장할 변경 없음");
		a("path_copied", "已复制路径: {0}", "Path copied: {0}", "パスをコピーしました: {0}", "경로 복사됨: {0}");
		a("recent_cleared", "已清除最近文件列表", "Recent list cleared", "最近の一覧を消去しました", "최근 목록 지움");
		a("clear_recent", "清除全部最近文件", "Clear recent files", "最近のファイルをすべて消去", "최근 파일 모두 지우기");
		a("no_file", "当前没有打开的文件。", "No file is open.", "ファイルが開かれていません。", "열린 파일이 없습니다.");
		a("no_print", "没有可打印的文档。", "Nothing to print.", "印刷できる文書がありません。", "인쇄할 문서가 없습니다.");
		a("unsupported_type", "不支持的文件类型:\n{0}\n\n支持: .pdf .docx .xlsx",
			"Unsupported file type:\n{0}\n\nSupported: .pdf .docx .xlsx",
			"未対応の種類:\n{0}\n\n対応: .pdf .docx .xlsx",
			"지원하지 않는 형식:\n{0}\n\n지원: .pdf .docx .xlsx");
		a("file_missing", "文件不存在:\n{0}", "File not found:\n{0}", "ファイルがありません:\n{0}", "파일이 없습니다:\n{0}");
		a("file_missing_removed", "文件不存在，已从最近列表移除:\n{0}",
			"File missing; removed from recent:\n{0}",
			"ファイルがなく、最近の一覧から削除しました:\n{0}",
			"파일이 없어 최근 목록에서 제거했습니다:\n{0}");
		a("save_failed", "保存失败: {0}", "Save failed: {0}", "保存に失敗: {0}", "저장 실패: {0}");
		a("print_failed", "打印失败: {0}", "Print failed: {0}", "印刷に失敗: {0}", "인쇄 실패: {0}");
		a("copy_failed", "复制失败: {0}", "Copy failed: {0}", "コピーに失敗: {0}", "복사 실패: {0}");
		a("explorer_failed", "无法打开资源管理器: {0}", "Cannot open Explorer: {0}", "エクスプローラーを開けません: {0}", "탐색기를 열 수 없음: {0}");
		a("tab_tip", "{0}\n拖动可排序；拖出窗口外可拆分为独立窗口",
			"{0}\nDrag to reorder; drag out to tear off a window",
			"{0}\nドラッグで並べ替え／外へ出して分離",
			"{0}\n끌어서 정렬; 밖으로 끌면 창 분리");
		a("tab_close", "关闭", "Close", "閉じる", "닫기");

		// —— 对话框 ——
		a("open_doc", "打开文档", "Open document", "文書を開く", "문서 열기");
		a("pdf_pro_title", "PDF 专业编辑", "PDF Pro Editor", "PDF プロ編集", "PDF 전문 편집");
		a("pdf_pro_need", "请先打开一个 PDF 文件，或从菜单选择 PDF。",
			"Open a PDF first, or pick one from the menu.",
			"先に PDF を開くか、メニューから選択してください。",
			"먼저 PDF를 열거나 메뉴에서 선택하세요.");
		a("pdf_pro_fail", "无法打开专业编辑: {0}", "Cannot open pro editor: {0}", "プロ編集を開けません: {0}", "전문 편집을 열 수 없음: {0}");
		a("confirm_save_pdf", "有未保存的 PDF 修改，是否保存？",
			"Unsaved PDF changes. Save?",
			"未保存の PDF の変更があります。保存しますか？",
			"저장되지 않은 PDF 변경이 있습니다. 저장할까요?");
		a("confirm_save_xlsx", "有未保存的表格修改，是否保存？",
			"Unsaved spreadsheet changes. Save?",
			"未保存の表の変更があります。保存しますか？",
			"저장되지 않은 표 변경이 있습니다. 저장할까요?");
		a("msg_edit_text_first", "请先在阅读模式下拖选要修改的文字，再进入编辑并点此按钮。\n也可直接用「添加文字」在页面上点选位置。",
			"Select text in read mode first, then enter edit and click this button.\nOr use Add Text and click the page.",
			"先に閲覧モードで文字を選択してから編集してください。\nまたは「テキスト追加」でページをクリック。",
			"읽기 모드에서 먼저 텍스트를 선택한 뒤 편집하세요.\n또는 「텍스트 추가」로 페이지를 클릭.");

		// —— 设置 ——
		a("settings_title", "系统参数", "Settings", "設定", "설정");
		a("ok", "确定", "OK", "OK", "확인");
		a("cancel", "取消", "Cancel", "キャンセル", "취소");
		a("theme_section", "界面主题（10 套）", "Themes (10)", "テーマ（10種）", "테마(10종)");
		a("theme_hint", "点击卡片预览，确定后保存。", "Click a card to preview; OK to save.", "カードをクリックしてプレビュー、OK で保存。", "카드를 눌러 미리보기, 확인 시 저장.");
		a("startup_section", "启动与行为", "Startup & behavior", "起動と動作", "시작 및 동작");
		a("restore_tabs", "启动时恢复上次打开的标签", "Restore last open tabs on startup", "起動時に前回のタブを復元", "시작 시 이전 탭 복원");
		a("show_side", "打开文档时默认显示目录侧栏", "Show outline when opening a document", "文書を開くと目次を表示", "문서 열 때 목차 표시");
		a("remember_win", "记住窗口位置与大小", "Remember window position and size", "ウィンドウ位置とサイズを記憶", "창 위치와 크기 기억");
		a("ui_font_section", "界面字体", "UI font", "UI フォント", "UI 글꼴");
		a("ui_font_hint", "影响菜单、工具栏、目录等界面文字（默认 12）。不影响 PDF/DOCX 正文。",
			"Affects menus, toolbar, outline (default 12). Does not change document body text.",
			"メニュー・ツールバー・目次など（既定 12）。本文の文字サイズは変わりません。",
			"메뉴·도구 모음·목차 등(기본 12). 문서 본문 크기는 변경하지 않습니다.");
		a("font_size_px", "字号（px）", "Size (px)", "サイズ (px)", "크기 (px)");
		a("lang_section", "界面语言", "UI language", "表示言語", "표시 언어");
		a("lang_hint", "切换后立即生效并保存。支持中文、English、日本語、한국어。",
			"Applies and saves immediately. Supports Chinese, English, Japanese, Korean.",
			"切り替えはすぐ反映・保存されます。中/英/日/韓に対応。",
			"전환 즉시 적용·저장. 중/영/일/한 지원.");
		a("notes_section", "说明", "Notes", "説明", "설명");
		a("settings_notes", "设置保存在本机 %LocalAppData%\\DocviewWPF\\settings.json。\n主题立即作用于标题栏、工具栏、菜单与强调色；文档内容样式仍以文件为准。",
			"Settings are stored in %LocalAppData%\\DocviewWPF\\settings.json.\nTheme applies to chrome immediately; document styles stay as in the file.",
			"設定は %LocalAppData%\\DocviewWPF\\settings.json に保存されます。\nテーマは枠にすぐ反映。本文の体裁はファイルに従います。",
			"설정은 %LocalAppData%\\DocviewWPF\\settings.json 에 저장됩니다.\n테마는 UI에 즉시 적용되며, 문서 스타일은 파일 기준입니다.");

		// —— 关于 ——
		a("about_body", "DocviewWPF {0}\n\n轻量多标签文档阅读器（.NET 4.8 WPF）\n支持 PDF / DOCX / XLSX\n\n{1}",
			"DocviewWPF {0}\n\nLightweight multi-tab document viewer (.NET 4.8 WPF)\nSupports PDF / DOCX / XLSX\n\n{1}",
			"DocviewWPF {0}\n\n軽量マルチタブ文書ビューア（.NET 4.8 WPF）\nPDF / DOCX / XLSX 対応\n\n{1}",
			"DocviewWPF {0}\n\n경량 다중 탭 문서 뷰어(.NET 4.8 WPF)\nPDF / DOCX / XLSX 지원\n\n{1}");
		a("about_features", "· 工具 → PDF 专业编辑 / 系统参数\n· 查找高亮 · 阅读进度 · 单实例\n· 界面语言：中 / 英 / 日 / 韩",
			"· Tools → PDF Pro Editor / Settings\n· Find highlight · Reading progress · Single instance\n· UI languages: zh / en / ja / ko",
			"· ツール → PDF プロ編集 / 設定\n· 検索ハイライト · 読書位置 · 単一インスタンス\n· 表示言語: 中/英/日/韓",
			"· 도구 → PDF 전문 편집 / 설정\n· 찾기 강조 · 읽기 위치 · 단일 인스턴스\n· 표시 언어: 중/영/일/한");

		// —— PDF 专业窗（常用） ——
		a("pro_save", "保存", "Save", "保存", "저장");
		a("pro_save_as", "另存为…", "Save as…", "名前を付けて保存…", "다른 이름으로 저장…");
		a("pro_undo", "撤销", "Undo", "元に戻す", "실행 취소");
		a("pro_redo", "重做", "Redo", "やり直し", "다시 실행");
		a("pro_select", "选择", "Select", "選択", "선택");
		a("pro_text", "文字", "Text", "テキスト", "텍스트");
		a("pro_image", "图片", "Image", "画像", "이미지");
		a("pro_whiteout", "遮盖", "Whiteout", "白塗り", "가리기");
		a("pro_rect", "矩形", "Rectangle", "矩形", "사각형");
		a("pro_delete", "删除", "Delete", "削除", "삭제");
		a("pro_dup", "复制", "Duplicate", "複製", "복제");
		a("pro_rotate90", "旋转90°", "Rotate 90°", "90°回転", "90° 회전");
		a("pro_larger", "放大", "Larger", "拡大", "확대");
		a("pro_smaller", "缩小", "Smaller", "縮小", "축소");
		a("pro_fit_w", "适宽", "Fit width", "幅に合わせる", "너비 맞춤");
		a("pro_new_page", "新页", "New page", "新しいページ", "새 페이지");
		a("pro_del_page", "删页", "Delete page", "ページ削除", "페이지 삭제");
		a("pro_rot_page", "页旋转", "Rotate page", "ページ回転", "페이지 회전");
		a("pro_pages", "页面", "Pages", "ページ", "페이지");
		a("pro_props", "对象属性", "Object properties", "オブジェクト属性", "개체 속성");
		a("pro_text_content", "文字内容（矢量）", "Text content (vector)", "テキスト内容（ベクトル）", "텍스트 내용(벡터)");
		a("pro_apply_text", "应用文字修改", "Apply text change", "テキストを適用", "텍스트 적용");
		a("pro_font", "字体（改字/新建时嵌入）", "Font (embed on edit/new)", "フォント（編集時に埋め込み）", "글꼴(편집 시 포함)");
		a("pro_size", "字号", "Size", "サイズ", "크기");
		a("pro_fill", "文字/填充颜色", "Text / fill color", "文字/塗りつぶし色", "글자/채우기 색");
		a("pro_apply_fill", "应用到选中填色", "Apply fill to selection", "選択に塗りを適用", "선택에 채우기 적용");
		a("pro_page_objects", "本页对象（Ctrl/Shift 多选）", "Page objects (Ctrl/Shift multi)", "ページ内オブジェクト", "페이지 개체(Ctrl/Shift 다중)");
		a("pro_page_n", "第 {0} 页", "Page {0}", "{0} ページ", "{0}쪽");
		a("pro_tool_select", "工具: 选择（拖空白框选 · Ctrl+点多选）",
			"Tool: Select (drag to marquee · Ctrl+click multi)",
			"ツール: 選択（ドラッグで範囲 · Ctrl+クリック）",
			"도구: 선택(끌어서 영역 · Ctrl+클릭 다중)");
		a("pro_tool_text", "工具: 添加文字（点击放置）", "Tool: Add text (click to place)", "ツール: テキスト追加", "도구: 텍스트 추가");
		a("pro_tool_img", "工具: 插入图片（点击放置）", "Tool: Insert image (click to place)", "ツール: 画像挿入", "도구: 이미지 삽입");
		a("pro_tool_white", "工具: 遮盖（拖出矩形）", "Tool: Whiteout (drag rectangle)", "ツール: 白塗り", "도구: 가리기");
		a("pro_tool_rect", "工具: 矩形（拖出填充）", "Tool: Rectangle (drag fill)", "ツール: 矩形", "도구: 사각형");
		a("pro_unsaved", "有未保存修改，是否保存？", "Unsaved changes. Save?", "未保存の変更があります。保存しますか？", "저장되지 않은 변경이 있습니다. 저장할까요?");
		a("pro_help", "选择：\n· 空白处拖拽 = 框选\n· Ctrl+点击 = 加减选\n· 拖选中对象 = 整组移动\n· Delete 删除全部选中\n· 中文用微软雅黑等嵌入字体",
			"Select:\n· Drag empty area = marquee\n· Ctrl+click = toggle\n· Drag selection = move group\n· Delete removes selection\n· CJK uses embedded system fonts",
			"選択:\n· 空白をドラッグ = 範囲選択\n· Ctrl+クリック = 加減\n· 選択をドラッグ = まとめて移動\n· Delete で削除\n· 日本語はシステムフォント埋め込み",
			"선택:\n· 빈 곳 끌기 = 영역 선택\n· Ctrl+클릭 = 가감\n· 선택 끌기 = 일괄 이동\n· Delete 로 삭제\n· 한글은 시스템 글꼴 포함");

		return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase) {
			[Zh] = zh, [En] = en, [Ja] = ja, [Ko] = ko,
		};
	}
}
