using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Voice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 輸入區的語音訊息（web-protocol 的 voiceMessage，2026-10-23 使用者要求；不呼叫 Windows 的語音輸入）：網頁錄下使用者的聲音，
// 這台把錄音存進對話的附件、以本機的 faster-whisper 轉成逐字稿（IVoiceTranscriber），再當成一則訊息送進代理——
// 代理收到逐字稿、錄音檔的路徑與「先用文字複誦使用者說了什麼再照做」的說明（VoiceMessageRules.AgentText），畫面上使用者那則是「語音訊息（0:12）」＋錄音附件。
// - 本機（桌面的網頁）：voiceMessage {conversationId, dataUrl, wave?}（wave 是錄音時記下的波形，存在錄音附件上）。
// - 遠端（瀏覽器版、其他電腦的桌面）：錄音先上傳到伺服器，voiceMessage {conversationId, uploads:[錄音]} 轉給擁有者電腦；這台照 addUploaded 下載匯入，
//   處理完那一批之後辨識，再照遠端 send 送出（同樣的閘門、來源與結果；畫面文字同上）。
// 辨識期間 ImportingCount 加一（送出被拒、輸入區顯示「正在辨識語音…」）。辨識失敗時錄音留在輸入區的附件（可以直接送出讓代理自己處理）；
// 沒聽到說話的內容就刪掉錄音、提示再錄一次。中樞預設沒有辨識器（不顯示麥克風、遠端回 unsupported），只有 AppController 以 AttachVoice 接上真的
internal sealed partial class AgentHub
{
    private IVoiceTranscriber? voiceTranscriber;

    /// <summary>接上語音訊息的辨識器（AppController；測試給假的）。之後送的 init 帶 voiceInput:true</summary>
    internal void AttachVoice(IVoiceTranscriber transcriber) => voiceTranscriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));

    /// <summary>本機的 voiceMessage：存錄音、辨識、送出</summary>
    private async Task VoiceMessageAsync(IAgentView view, ConversationState? c, string? dataUrl, string? wave = null)
    {
        if (c == null)
            return;
        if (voiceTranscriber is not { } transcriber)
        {
            Toast("這台電腦沒有啟用語音訊息", isError: true, view: view);
            return;
        }
        if (!VoiceMessageRules.TryParseDataUrl(dataUrl, out var bytes, out var extension))
        {
            Toast($"錄音的格式讀不了，或超過 {AgentAttachmentRules.FormatSize(VoiceMessageRules.MaxBytes)}", isError: true, view: view);
            return;
        }

        var fileName = VoiceMessageRules.FileName(DateTime.Now, extension);
        var attachments = store.GetAttachmentDirectory(c.Meta.Id);
        var modelImages = store.GetModelImageDirectory(c.Meta.Id);
        c.ImportingCount++;
        PostComposer(c, "正在辨識語音…");
        AgentAttachment attachment;
        (VoiceTranscript? Transcript, string? Failure) result;
        try
        {
            try
            {
                attachment = await Task.Run(() => AgentAttachmentProcessor.ImportBytesAsync(bytes, fileName, attachments, modelImages, CancellationToken.None));
                // 錄音時記下的波形（網頁的 wave；不合格式的不存）：使用者訊息的泡泡裡畫成波形條
                attachment.Waveform = VoiceMessageRules.NormalizeWaveform(wave);
            }
            catch (Exception ex)
            {
                Log.Warn($"對話 {c.Meta.Id} 的語音訊息存檔失敗：{ex.Message}");
                Toast($"錄音存檔失敗：{ex.Message}", isError: true, view: view);
                return;
            }
            result = await TranscribeVoiceAsync(transcriber, attachment.StoredPath);
        }
        finally
        {
            c.ImportingCount--;
            if (!disposed)
                PostComposer(c);
        }

        if (!IsLiveConversation(c))
        {
            DeleteFolderInBackground(c.Meta.Id, quiet: true);   // 辨識期間對話被刪或草稿被丟棄：不留下孤兒資料夾
            return;
        }
        await FinishVoiceAsync(view, c, attachment, result.Transcript, result.Failure, remote: null);
    }

    /// <summary>遠端的 voiceMessage（ConversationRemoteHandler）：照 addUploaded 下載匯入，那一批處理完再辨識、送出</summary>
    private void RemoteVoiceMessage(RemoteViewContext context, IAgentView view, ConversationState c, JObject command)
    {
        if (voiceTranscriber is null)
        {
            context.Reject(RemoteResultCodes.Unsupported, "那台電腦沒有啟用語音訊息（要更新 app）", RemoteGateSteps.Handler);
            return;
        }
        var uploads = ParseRemoteUploads(command["uploads"] as JArray);
        if (uploads.Count != 1 || !VoiceMessageRules.IsVoiceFileName(uploads[0].Name) || uploads[0].Size > VoiceMessageRules.MaxBytes)
        {
            context.Reject(RemoteResultCodes.Invalid, "語音訊息要剛好一個錄音檔", RemoteGateSteps.Handler);
            return;
        }
        if (StartRemoteUploads(c, uploads) is { } refused)
        {
            context.Reject(refused.Code, refused.Message, RemoteGateSteps.Handler);
            return;
        }
        var uploadId = uploads[0].UploadId;
        c.AfterRemoteUploads.Add(() => _ = RemoteVoiceContinueAsync(context, view, c, uploadId));
    }

    /// <summary>錄音下載匯入完（UI 執行緒）：找到它成為的附件、辨識、送出</summary>
    private async Task RemoteVoiceContinueAsync(RemoteViewContext context, IAgentView view, ConversationState c, string uploadId)
    {
        try
        {
            if (voiceTranscriber is not { } transcriber || !c.RemoteUploadAttachments.Remove(uploadId, out var attachmentId)
                || c.PendingAttachments.FirstOrDefault(a => a.Id == attachmentId) is not { } attachment)
            {
                const string Missing = "錄音沒有傳到那台電腦（下載或匯入失敗）";
                Toast(Missing, isError: true, view: view);
                context.Reject(RemoteResultCodes.Unsupported, Missing, RemoteGateSteps.Handler);
                return;
            }

            c.ImportingCount++;
            PostComposer(c, "正在辨識語音…");
            (VoiceTranscript? Transcript, string? Failure) result;
            try
            {
                result = await TranscribeVoiceAsync(transcriber, attachment.StoredPath);
            }
            finally
            {
                c.ImportingCount--;
                if (!disposed)
                    PostComposer(c);
            }
            if (!IsLiveConversation(c))
            {
                context.Reject(RemoteResultCodes.Conflict, "這個對話在辨識途中被刪除了", RemoteGateSteps.Handler);
                return;
            }
            await FinishVoiceAsync(view, c, attachment, result.Transcript, result.Failure, context);
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 處理語音訊息時出錯", ex);
            context.Fail("電腦處理語音訊息時出錯，訊息可能沒有送出；請看對話的內容確認", RemoteGateSteps.Handler);
        }
    }

    /// <summary>背景辨識；失敗回給人看的說明（不丟例外）</summary>
    private static async Task<(VoiceTranscript? Transcript, string? Failure)> TranscribeVoiceAsync(IVoiceTranscriber transcriber, string path)
    {
        try
        {
            return (await Task.Run(() => transcriber.TranscribeAsync(path, CancellationToken.None)), null);
        }
        catch (VoiceTranscribeException ex)
        {
            Log.Warn($"語音辨識失敗：{ex.Message}");
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("語音辨識出錯", ex);
            return (null, "語音辨識出錯：" + ex.Message);
        }
    }

    /// <summary>
    /// 辨識完（UI 執行緒）：失敗→錄音放在輸入區的附件並說明；沒有內容→刪掉錄音並說明；有內容→當成一則訊息送出。
    /// <paramref name="remote"/> 是 null＝本機（錄音還不在輸入區，這裡才放進去）；否則錄音已是網頁上傳的待送附件、結果回給那個指令
    /// </summary>
    private async Task FinishVoiceAsync(IAgentView view, ConversationState c, AgentAttachment attachment, VoiceTranscript? transcript, string? failure,
        RemoteViewContext? remote)
    {
        if (failure != null || transcript == null)
        {
            if (remote == null)
                c.PendingAttachments.Add(attachment);
            PostComposer(c);
            var message = $"{failure ?? "語音辨識失敗"}。錄音已放進輸入區的附件：可以直接送出讓代理自己處理，或移除後再錄一次";
            // 遠端：提示經回應檢視轉給發起的瀏覽器或電腦（結果本身不帶說明給網頁）
            Toast(message, isError: true, view: view);
            remote?.Reject(RemoteResultCodes.Unsupported, message, RemoteGateSteps.Handler);
            return;
        }

        var text = VoiceMessageRules.CleanTranscript(transcript.Text);
        if (text.Length == 0)
        {
            if (remote != null)
            {
                c.PendingAttachments.Remove(attachment);
                c.RemoteUploadedAttachmentIds.Remove(attachment.Id);
            }
            DeleteAttachmentFiles(attachment);
            PostComposer(c);
            const string Empty = "沒有聽到說話的內容，請再錄一次";
            Toast(Empty, isError: true, view: view);
            remote?.Reject(RemoteResultCodes.Invalid, Empty, RemoteGateSteps.Handler);
            return;
        }

        attachment.DurationSeconds = transcript.DurationSeconds;
        if (remote == null)
            c.PendingAttachments.Add(attachment);
        var display = VoiceMessageRules.DisplayText(transcript.DurationSeconds);
        var agentText = VoiceMessageRules.AgentText(text, transcript.DurationSeconds);
        var wasDraft = c.IsDraft;
        if (remote != null)
            await RemoteSendAsync(remote, view, c, agentText, display);
        else
            await SendAsync(view, c, agentText, displayText: display);
        // 送出被拒（帳戶沒登入、資料夾不在…）時錄音還在輸入區的附件：重送輸入區，網頁才看得到它（拒絕本身不重送輸入區）
        if (IsLiveConversation(c))
            PostComposer(c);

        // 新對話的第一則是語音：標題改用逐字稿（送出時取的是畫面文字「語音訊息（0:12）」）
        if (wasDraft && !c.IsDraft && IsLiveConversation(c) && c.Meta.Title == AgentHubRules.MakeTitle(display))
            Rename(c, AgentHubRules.MakeTitle(text));
    }

    /// <summary>對話還在（沒被刪、草稿沒被丟、程式沒在關）</summary>
    private bool IsLiveConversation(ConversationState c)
        => !disposed && conversations.TryGetValue(c.Meta.Id, out var live) && ReferenceEquals(live, c);

    /// <summary>刪掉附件的檔案（含給模型看的圖；語音的錄音沒有）</summary>
    private void DeleteAttachmentFiles(AgentAttachment attachment)
    {
        foreach (var path in attachment.ModelImagePaths.Append(attachment.StoredPath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RevokeMedia(path);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
