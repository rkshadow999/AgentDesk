import { describe, expect, it } from "vitest";
import { createTranslator } from "../src/i18n";
import enUs from "../src/locales/en-US.json";
import zhCn from "../src/locales/zh-CN.json";

describe("createTranslator", () => {
  it("uses Simplified Chinese and falls back to English", () => {
    const translate = createTranslator(
      { greeting: "Hello", engineOffline: "Engine offline" },
      { greeting: "你好" }
    );

    expect(translate("greeting")).toBe("你好");
    expect(translate("engineOffline")).toBe("Engine offline");
    expect(translate("missingKey")).toBe("missingKey");
  });

  it("provides provider settings labels in Chinese and English", () => {
    expect(zhCn).toMatchObject({
      baseUrl: "Base URL",
      model: "模型",
      backend: "后端",
      providerCredentialStored: "已为这个 Base URL 安全保存凭据。保存设置时将复用它。",
      replaceProviderCredential: "替换已保存的 API Key",
      allowInsecureHttp: "允许通过不安全 HTTP 发送 API Key"
    });
    expect(enUs).toMatchObject({
      baseUrl: "Base URL",
      model: "Model",
      backend: "Backend",
      providerCredentialStored:
        "A credential is securely stored for this Base URL and will be reused when saving.",
      replaceProviderCredential: "Replace the saved API Key",
      allowInsecureHttp: "Allow sending the API Key over insecure HTTP"
    });
  });

  it("keeps the runtime dashboard resource keys aligned in both languages", () => {
    expect(Object.keys(zhCn).sort()).toEqual(Object.keys(enUs).sort());
    expect(zhCn).toMatchObject({
      agentDashboard: "智能体活动",
      backgroundTasks: "后台任务",
      runningSubagents: "运行中智能体"
    });
    expect(enUs).toMatchObject({
      agentDashboard: "Agent activity",
      backgroundTasks: "Background tasks",
      runningSubagents: "Running agents"
    });
  });

  it("localizes font scale and full access preferences", () => {
    expect(zhCn).toMatchObject({
      fontScale: "界面字体大小",
      fontScale_90: "较小 (90%)",
      fontScale_100: "标准 (100%)",
      fontScale_110: "舒适 (110%)",
      fontScale_125: "较大 (125%)",
      fontScale_140: "特大 (140%)",
      fullAccessEnabled: "启用完全访问",
      fullAccessActive: "完全访问已开启"
    });
    expect(enUs).toMatchObject({
      fontScale: "Interface font size",
      fontScale_90: "Smaller (90%)",
      fontScale_100: "Standard (100%)",
      fontScale_110: "Comfortable (110%)",
      fontScale_125: "Large (125%)",
      fontScale_140: "Extra large (140%)",
      fullAccessEnabled: "Enable full access",
      fullAccessActive: "Full access enabled"
    });
  });

  it("localizes trusted background update availability", () => {
    expect(zhCn.backgroundUpdateAvailable).toBe(
      "已在后台暂存可信更新，可随时安装并重启"
    );
    expect(enUs.backgroundUpdateAvailable).toBe(
      "A trusted update was staged in the background and is ready to install"
    );
  });

  it("localizes workspace instructions and file references", () => {
    expect(zhCn).toMatchObject({
      agentsTitle: "项目指令",
      agentsEditor: "编辑 AGENTS.md",
      saveAgentsInstructions: "保存 AGENTS.md",
      fileSearchResults: "文件搜索结果",
      referencedFiles: "已引用文件"
    });
    expect(enUs).toMatchObject({
      agentsTitle: "Project instructions",
      agentsEditor: "Edit AGENTS.md",
      saveAgentsInstructions: "Save AGENTS.md",
      fileSearchResults: "File search results",
      referencedFiles: "Referenced files"
    });
  });
});
