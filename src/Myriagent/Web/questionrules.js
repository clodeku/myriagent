// 問卷卡的純規則（2026-11-05 使用者要求：單選題選了選項之後，還能另外補一段說明；tools/ui-probe/questionrules.test.mjs）

/** 合併進答案的補充說明開頭（同宿主的 QuestionNoteRules.DisplayPrefix：畫面、存檔、Codex 都看到這一筆） */
export const NOTE_PREFIX = '補充說明：';

/** 一題的補充說明上限（同 QuestionNoteRules.MaxLength） */
export const NOTE_MAX = 4000;

/**
 * 這一題要不要給「補充說明（選填）」：有選項、不是密碼類（補充會留在畫面與存檔）、不是外部來源的提問（MCP 的表單沒有地方放補充）。
 * 沒有選項的題目本來就只有自由輸入，不另外給
 */
export function noteFieldShown(q, asker) {
  return !!q && !q.secret && !asker && Array.isArray(q.options) && q.options.length > 0;
}

/**
 * 送出的 notes：{題目 id: [文字]}（形狀同 answers），只收有補充欄的題目、去掉前後空白、超過上限截斷。
 * 沒有任何補充回 null：不帶這個欄位（舊版的電腦收到不認得的欄位會拒絕整個 answer）
 */
export function buildNotes(questions, notes, asker) {
  const out = {};
  for (const q of Array.isArray(questions) ? questions : []) {
    if (!noteFieldShown(q, asker)) continue;
    const text = typeof (notes && notes[q.id]) === 'string' ? notes[q.id].trim() : '';
    if (text) out[q.id] = [text.length > NOTE_MAX ? text.slice(0, NOTE_MAX) : text];
  }
  return Object.keys(out).length ? out : null;
}

/** 已回答的值分成答案（晶片）與補充說明（另起一行）；不是陣列回兩個空的 */
export function splitAnswer(values) {
  const answers = [];
  const notes = [];
  for (const v of Array.isArray(values) ? values : []) {
    if (typeof v !== 'string') continue;
    if (v.startsWith(NOTE_PREFIX)) notes.push(v.slice(NOTE_PREFIX.length));
    else answers.push(v);
  }
  return { answers, notes };
}

/**
 * 問卷的作答進度（第十一批，2026-11-08：多題的問卷在標頭下方一題一格）：每題 { answered, note }——
 * answered＝選了選項或「其他」有打字（同送出時的判斷），note＝補充說明有填（只看去掉空白後有沒有字）
 */
export function progressCells(questions, st) {
  const sel = (st && st.sel) || {};
  const other = (st && st.other) || {};
  const note = (st && st.note) || {};
  return (Array.isArray(questions) ? questions : []).map((q) => {
    const id = q && q.id;
    const picked = Array.isArray(sel[id]) && sel[id].length > 0;
    const typed = typeof other[id] === 'string' && other[id].trim() !== '';
    return { answered: picked || typed, note: typeof note[id] === 'string' && note[id].trim() !== '' };
  });
}
