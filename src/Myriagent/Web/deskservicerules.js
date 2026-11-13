// 遠端桌面的鎖定畫面支援（設定頁同步區的一塊；web-protocol 的 deskService）：宿主的狀態 → 畫面上的文字與按鈕（純規則）。
// 那是一個以系統權限執行的 Windows 服務：一般權限的 app 看不到也操作不了鎖定畫面與系統管理員確認畫面（UAC），
// 裝了它，遠端桌面在那些畫面上才看得到、才能輸入。安裝、更新、移除都要有人在這台按 UAC

/** 設定頁要不要顯示這一區（宿主沒接、功能還沒開放給這台時不顯示） */
export function deskServiceShown(s) {
  return !!(s && typeof s === 'object' && s.offered === true);
}

/** 版本的寫法：0.2.950（abc1234）；開發版沒有提交 */
export function deskServiceVersion(version, build) {
  const v = typeof version === 'string' && version ? version : '?';
  return !build || build === 'dev' ? v + '（開發版）' : v + '（' + build + '）';
}

/**
 * 狀態 → { tone, label, detail, actions }。tone 沿用帳戶卡的膠囊色；actions 的 act 是送給宿主的 action（install 也是更新與重新安裝）。
 * 進行中（busy）時沒有按鈕
 */
export function deskServiceSummary(s) {
  if (!s || typeof s !== 'object' || s.known !== true) {
    if (s && s.busy === 'install') return busy('安裝中…', '請在 Windows 的系統管理員確認視窗按「是」。');
    return { tone: 'st-checking', label: '檢查中…', detail: '', actions: [] };
  }
  if (s.busy === 'install') return busy(s.installed ? '更新中…' : '安裝中…', '請在 Windows 的系統管理員確認視窗按「是」。');
  if (s.busy === 'uninstall') return busy('移除中…', '請在 Windows 的系統管理員確認視窗按「是」。');
  if (s.busy === 'check') return busy('檢查中…', '');

  const remove = { act: 'uninstall', label: '移除…' };
  if (!s.installed) {
    return s.canInstall
      ? { tone: 'st-checking', label: '未安裝', detail: '', actions: [{ act: 'install', label: '安裝…' }] }
      : { tone: 'st-checking', label: '未安裝', detail: '這一份 app 沒有帶服務的檔案，不能安裝。', actions: [] };
  }
  const have = deskServiceVersion(s.version, s.build);
  const reinstall = s.canInstall ? [{ act: 'install', label: '重新安裝…' }] : [];
  if (!s.running) {
    return { tone: 'st-error', label: '沒有在執行', detail: '已安裝 ' + have + '，但服務沒有回應。', actions: [...reinstall, remove] };
  }
  if (s.system === false) {
    return { tone: 'st-notLoggedIn', label: '不是系統權限', detail: '服務在執行，但不是以系統權限執行，看不到鎖定畫面。', actions: [...reinstall, remove] };
  }
  if (s.update && s.canInstall) {
    const next = s.bundled ? deskServiceVersion(s.bundled.version, s.bundled.build) : '';
    return { tone: 'st-notLoggedIn', label: '有更新', detail: '已安裝 ' + have + '，這一版的 app 帶的是 ' + next + '。', actions: [{ act: 'install', label: '更新…' }, remove] };
  }
  return { tone: 'st-ready', label: '執行中', detail: '已安裝 ' + have + '。', actions: [remove] };
}

function busy(label, detail) {
  return { tone: 'st-busy', label, detail, actions: [] };
}

/** 服務看到的輸入桌面（診斷用的一行）；沒有回 null */
export function deskServiceProbeText(s) {
  const p = s && s.probe;
  if (!p || typeof p !== 'object') return null;
  if (typeof p.desktop === 'string' && p.desktop) {
    return '服務看到的輸入桌面：' + p.desktop + (p.secure ? '（鎖定或系統管理員確認畫面）' : '（使用者的桌面）');
  }
  return '服務開不了輸入桌面（錯誤碼 ' + (Number(p.error) || 0) + '）';
}

/** 按下按鈕前的確認框（install 依目前的狀態是安裝、更新或重新安裝） */
export function deskServiceConfirm(act, s) {
  if (act === 'uninstall') {
    return {
      title: '移除鎖定畫面支援',
      message: '會停止並移除這台的服務「MyriagentDesk」。移除後遠端桌面照常可用，只是這台鎖定或出現系統管理員確認畫面時不能操作。',
      detail: 'Windows 會跳出系統管理員確認（UAC），要在這台按「是」。',
      confirmLabel: '移除',
    };
  }
  const installed = !!(s && s.installed);
  return {
    title: installed ? '更新鎖定畫面支援' : '安裝鎖定畫面支援',
    message: '會在這台安裝一個以系統權限執行的 Windows 服務「MyriagentDesk」（放在 Program Files，開機自動啟動）。它只接受這台登入的使用者的 Clode Myriagent 連線，不連網路。',
    detail: 'Windows 會跳出系統管理員確認（UAC），要在這台按「是」。之後可以在這裡移除。',
    confirmLabel: installed ? '更新' : '安裝',
  };
}
