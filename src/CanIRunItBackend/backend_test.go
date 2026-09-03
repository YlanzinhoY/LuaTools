package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestParseRequirementsCleansSteamHTML(t *testing.T) {
	raw := json.RawMessage(`{"minimum":"<strong>Minimum:</strong><br>OS: Windows 11<br>RAM: 8 GB &amp; up","recommended":"<b>Recommended:</b><br>RAM: 16 GB"}`)
	result := parseRequirements(raw)
	if result.Minimum != "Minimum:\nOS: Windows 11\nRAM: 8 GB & up" {
		t.Fatalf("unexpected minimum requirements: %q", result.Minimum)
	}
	if result.Recommended != "Recommended:\nRAM: 16 GB" {
		t.Fatalf("unexpected recommended requirements: %q", result.Recommended)
	}
}

func TestParseModelAnalysisNormalizesUnknownValues(t *testing.T) {
	result, err := parseModelAnalysis("```json\n" + `{"verdict":"minimum","confidence":"certain","summary":"Runs.","components":[{"component":"GPU","status":"maybe","explanation":"Unclear"}]}` + "\n```")
	if err != nil {
		t.Fatalf("parse failed: %+v", err)
	}
	if result.Confidence != "low" || result.Components[0].Status != "unknown" {
		t.Fatalf("unsafe enum normalization: %+v", result)
	}
	if result.Recommendations == nil || result.Caveats == nil {
		t.Fatal("collections must be non-nil for stable JSON")
	}
}

func TestParseModelAnalysisExtractsJSONFromLingResponse(t *testing.T) {
	content := `<think>I should compare the components. A scratch object such as {not JSON} is not the answer.</think>
Here is the requested result:
` + "```json\n" + `{"verdict":"Recommended","confidence":"HIGH","summary":"It should run well.","components":[{"component":"GPU","status":"MEETS","explanation":"Suitable."}],"recommendations":[],"caveats":["This is an estimate."]}` + "\n```\nDone."

	result, err := parseModelAnalysis(content)
	if err != nil {
		t.Fatalf("parse failed: %+v", err)
	}
	if result.Verdict != "recommended" || result.Confidence != "high" || result.Components[0].Status != "meets" {
		t.Fatalf("response was not normalized: %+v", result)
	}
}

func TestParseModelAnalysisSkipsUnrelatedJSONObject(t *testing.T) {
	content := `Debug metadata: {"elapsed": 2}. Final: {"verdict":"minimum","confidence":"medium","summary":"Playable.","components":[]}`
	result, err := parseModelAnalysis(content)
	if err != nil || result.Verdict != "minimum" {
		t.Fatalf("did not find schema-compatible object: result=%+v err=%+v", result, err)
	}
}

func TestAnalyzeUsesOpenRouterFreeDeepSeekAndKeepsAuthoritativeData(t *testing.T) {
	var authorization string
	var requestedModel string
	var reasoningExcluded bool
	var maxTokens float64
	var systemPrompt string
	var userPrompt string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/steam":
			_, _ = w.Write([]byte(`{"42":{"success":true,"data":{"name":"Steam Name","pc_requirements":{"minimum":"RAM: 8 GB","recommended":"RAM: 16 GB"}}}}`))
		case "/openrouter":
			authorization = r.Header.Get("Authorization")
			var request map[string]any
			_ = json.NewDecoder(r.Body).Decode(&request)
			requestedModel, _ = request["model"].(string)
			if messages, ok := request["messages"].([]any); ok && len(messages) >= 2 {
				if systemMessage, ok := messages[0].(map[string]any); ok {
					systemPrompt, _ = systemMessage["content"].(string)
				}
				if userMessage, ok := messages[1].(map[string]any); ok {
					userPrompt, _ = userMessage["content"].(string)
				}
			}
			if reasoning, ok := request["reasoning"].(map[string]any); ok {
				reasoningExcluded, _ = reasoning["exclude"].(bool)
			}
			maxTokens, _ = request["max_tokens"].(float64)
			_, _ = w.Write([]byte(`{"choices":[{"message":{"content":"{\"verdict\":\"recommended\",\"confidence\":\"high\",\"summary\":\"Good fit.\",\"components\":[],\"recommendations\":[],\"caveats\":[]}"}}]}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	b := newBackend(server.Client())
	b.steamURL = server.URL + "/steam"
	b.openRouterURL = server.URL + "/openrouter"
	b.detect = func(context.Context) (hardwareInfo, *backendError) {
		return hardwareInfo{CPU: "Test CPU", MemoryGB: 32, GPUs: []gpuInfo{{Name: "Test GPU"}}, OS: "Windows 11"}, nil
	}
	result, err := b.analyze(context.Background(), analyzeInput{AppID: 42, GameName: "Lua Name", Language: "pt-BR", APIKey: "secret"})
	if err != nil {
		t.Fatalf("analysis failed: %+v", err)
	}
	if authorization != "Bearer secret" {
		t.Fatalf("missing bearer auth: %q", authorization)
	}
	if requestedModel != defaultModel {
		t.Fatalf("wrong model: %q", requestedModel)
	}
	if !reasoningExcluded {
		t.Fatal("reasoning output must be excluded from the response")
	}
	if maxTokens < 4000 {
		t.Fatalf("completion budget is too small: %.0f", maxTokens)
	}
	if !strings.Contains(systemPrompt, `Brazilian Portuguese (português do Brasil) (BCP-47 "pt-BR")`) ||
		!strings.Contains(systemPrompt, "Do not write those fields in English") {
		t.Fatalf("selected UI language is not a trusted system instruction: %q", systemPrompt)
	}
	if strings.Contains(userPrompt, "response_language") {
		t.Fatal("response language must not be embedded in the untrusted data payload")
	}
	if result.GameName != "Lua Name" || result.Hardware.CPU != "Test CPU" || result.Requirements.Recommended != "RAM: 16 GB" {
		t.Fatalf("authoritative fields changed: %+v", result)
	}
}

func TestLanguageDisplayNameCoversSupportedVariants(t *testing.T) {
	tests := map[string]string{
		"pt-BR":   "Brazilian Portuguese (português do Brasil)",
		"pt-PT":   "European Portuguese (português de Portugal)",
		"es-419":  "Latin American Spanish (español latinoamericano)",
		"zh-Hans": "Simplified Chinese (简体中文)",
	}
	for tag, expected := range tests {
		if actual := languageDisplayName(tag); actual != expected {
			t.Errorf("languageDisplayName(%q) = %q, want %q", tag, actual, expected)
		}
	}
	if actual := languageDisplayName("x-custom"); actual != "x-custom" {
		t.Fatalf("unknown language tag changed: %q", actual)
	}
}

func TestOpenRouterAuthenticationErrorIsSafe(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusUnauthorized)
		_, _ = w.Write([]byte(`{"error":{"message":"upstream detail"}}`))
	}))
	defer server.Close()

	b := newBackend(server.Client())
	b.openRouterURL = server.URL
	_, err := b.askOpenRouter(context.Background(), analyzeInput{AppID: 1, APIKey: "bad"}, "Game", hardwareInfo{}, gameRequirements{Minimum: "CPU"})
	if err == nil || err.Code != "invalid_api_key" || strings.Contains(err.Message, "upstream") {
		t.Fatalf("unexpected error: %+v", err)
	}
}
