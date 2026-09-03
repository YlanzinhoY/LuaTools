package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func validAnalysisJSON(verdict, confidence string) string {
	components := make([]componentAssessment, 0, 5)
	for _, component := range []string{"CPU", "GPU", "RAM", "OS", "Storage"} {
		components = append(components, componentAssessment{
			Component: component, Status: "meets", Explanation: component + " is suitable.",
		})
	}
	value, _ := json.Marshal(modelAnalysis{
		Verdict: verdict, Confidence: confidence, Summary: "Playable.", Components: components,
		Recommendations: []string{}, Caveats: []string{},
	})
	return string(value)
}

func writeToolCompletion(w http.ResponseWriter, arguments string) {
	_ = json.NewEncoder(w).Encode(map[string]any{
		"choices": []any{map[string]any{
			"finish_reason": "tool_calls",
			"message": map[string]any{
				"content": nil,
				"tool_calls": []any{map[string]any{
					"function": map[string]any{"name": analysisToolName, "arguments": arguments},
				}},
			},
		}},
	})
}

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

func TestMinimumStorageGBParsesPublishedUnits(t *testing.T) {
	tests := []struct {
		requirements string
		want         float64
	}{
		{"Storage: 75 GB available space", 75},
		{"Hard Drive: 1.5 TB available space", 1536},
		{"Disk Space: 512 MB", 0.5},
		{"Storage: 2,5 GB", 2.5},
	}
	for _, test := range tests {
		got, ok := minimumStorageGB(test.requirements)
		if !ok || got != test.want {
			t.Errorf("minimumStorageGB(%q) = %v, %v; want %v, true", test.requirements, got, ok, test.want)
		}
	}
}

func TestDeterministicStorageCheckMarksInsufficientSpaceBelow(t *testing.T) {
	result, err := parseModelAnalysis(validAnalysisJSON("minimum", "medium"))
	if err != nil {
		t.Fatalf("parse fixture: %+v", err)
	}
	result.Components[4].Status = "unknown"

	applyDeterministicChecks(&result,
		hardwareInfo{SystemDriveFreeGB: 72.9},
		gameRequirements{Minimum: "Storage: 75 GB available space"})

	if result.Verdict != "poor" {
		t.Fatalf("below-minimum storage must make the verdict poor, got %q", result.Verdict)
	}
	if result.Components[4].Component != "Storage" || result.Components[4].Status != "below" {
		t.Fatalf("below-minimum storage must be marked below: %+v", result.Components[4])
	}
}

func TestDeterministicStorageCheckDoesNotFailAtMinimum(t *testing.T) {
	result, err := parseModelAnalysis(validAnalysisJSON("minimum", "medium"))
	if err != nil {
		t.Fatalf("parse fixture: %+v", err)
	}
	result.Components[4].Status = "unknown"

	applyDeterministicChecks(&result,
		hardwareInfo{SystemDriveFreeGB: 75},
		gameRequirements{Minimum: "Storage: 75 GB available space"})

	if result.Verdict != "minimum" || result.Components[4].Status != "unknown" {
		t.Fatalf("space at the minimum must not be forced below: %+v", result)
	}
}

func TestParseModelAnalysisNormalizesUnknownValues(t *testing.T) {
	content := strings.Replace(validAnalysisJSON("minimum", "certain"), `"status":"meets"`, `"status":"maybe"`, 1)
	result, err := parseModelAnalysis("```json\n" + content + "\n```")
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
` + "```json\n" + validAnalysisJSON("Recommended", "HIGH") + "\n```\nDone."

	result, err := parseModelAnalysis(content)
	if err != nil {
		t.Fatalf("parse failed: %+v", err)
	}
	if result.Verdict != "recommended" || result.Confidence != "high" || result.Components[0].Status != "meets" {
		t.Fatalf("response was not normalized: %+v", result)
	}
}

func TestParseModelAnalysisSkipsUnrelatedJSONObject(t *testing.T) {
	content := `Debug metadata: {"elapsed": 2}. Final: ` + validAnalysisJSON("minimum", "medium")
	result, err := parseModelAnalysis(content)
	if err != nil || result.Verdict != "minimum" {
		t.Fatalf("did not find schema-compatible object: result=%+v err=%+v", result, err)
	}
}

func TestAnalyzeUsesOpenRouterFreeLingAndKeepsAuthoritativeData(t *testing.T) {
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
			_ = json.NewEncoder(w).Encode(map[string]any{
				"choices": []any{map[string]any{"message": map[string]any{"content": validAnalysisJSON("recommended", "high")}}},
			})
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	b := newBackend(server.Client())
	b.cacheDir = t.TempDir()
	b.steamURL = server.URL + "/steam"
	b.openRouterURL = server.URL + "/openrouter"
	b.detect = func(context.Context) (hardwareInfo, *backendError) {
		return hardwareInfo{CPU: "Test CPU", MemoryGB: 32, GPUs: []gpuInfo{{Name: "Test GPU"}}, OS: "Windows 11"}, nil
	}
	result, err := b.analyze(context.Background(), analyzeInput{AppID: 42, GameName: "Lua Name", Language: "pt-BR", LanguageName: "Portuguese (Brazil)", APIKey: "secret"})
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
	if !strings.Contains(userPrompt, "required tool") {
		t.Fatal("request did not require the structured analysis tool")
	}
	if !strings.Contains(systemPrompt, `Portuguese (Brazil) (BCP-47 "pt-BR")`) ||
		!strings.Contains(systemPrompt, "Do not write those fields in English") {
		t.Fatalf("selected UI language is not a trusted system instruction: %q", systemPrompt)
	}
	for _, character := range systemPrompt {
		if character > 127 {
			t.Fatalf("system instructions must remain English/ASCII, found %q", character)
		}
	}
	if strings.Contains(userPrompt, "response_language") {
		t.Fatal("response language must not be embedded in the untrusted data payload")
	}
	if result.GameName != "Lua Name" || result.Hardware.CPU != "Test CPU" || result.Requirements.Recommended != "RAM: 16 GB" {
		t.Fatalf("authoritative fields changed: %+v", result)
	}
}

func TestParseToolAnalysisAcceptsStringAndObjectArguments(t *testing.T) {
	content := validAnalysisJSON("recommended", "high")
	encoded, _ := json.Marshal(content)
	for _, raw := range []json.RawMessage{json.RawMessage(content), encoded} {
		result, err := parseToolAnalysis(raw)
		if err != nil || result.Verdict != "recommended" || len(result.Components) != 5 {
			t.Fatalf("tool arguments failed: result=%+v err=%+v", result, err)
		}
	}
}

func TestPromptUsesLanguageSelectedByLuaTools(t *testing.T) {
	var systemPrompt string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var request map[string]any
		_ = json.NewDecoder(r.Body).Decode(&request)
		messages, _ := request["messages"].([]any)
		message, _ := messages[0].(map[string]any)
		systemPrompt, _ = message["content"].(string)
		writeToolCompletion(w, validAnalysisJSON("recommended", "high"))
	}))
	defer server.Close()

	b := newBackend(server.Client())
	b.openRouterURL = server.URL
	_, err := b.askOpenRouter(context.Background(), analyzeInput{
		APIKey: "secret", Language: "pl", LanguageName: "Polish (Poland)",
	}, "Game", hardwareInfo{}, gameRequirements{Minimum: "RAM: 8 GB"})
	if err != nil || !strings.Contains(systemPrompt, `TARGET OUTPUT LANGUAGE: Polish (Poland) (BCP-47 "pl")`) {
		t.Fatalf("LuaTools language was not used dynamically: prompt=%q err=%+v", systemPrompt, err)
	}
	if strings.Contains(systemPrompt, "Brazil") {
		t.Fatal("prompt leaked a hardcoded Brazilian language choice")
	}
}

func TestAnalyzeRetriesMalformedProviderOutput(t *testing.T) {
	requests := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		if requests == 1 {
			_, _ = w.Write([]byte(`{"choices":[{"finish_reason":"length","message":{"content":"{cut"}}]}`))
			return
		}
		writeToolCompletion(w, validAnalysisJSON("minimum", "high"))
	}))
	defer server.Close()

	b := newBackend(server.Client())
	b.cacheDir = t.TempDir()
	b.openRouterURL = server.URL
	result, err := b.askOpenRouter(context.Background(), analyzeInput{APIKey: "secret", Language: "en"},
		"Game", hardwareInfo{}, gameRequirements{Minimum: "RAM: 8 GB"})
	if err != nil || result.Verdict != "minimum" || requests != 2 {
		t.Fatalf("retry failed: result=%+v err=%+v requests=%d", result, err, requests)
	}
}

func TestAnalyzeReturnsInconclusiveFallbackAfterMalformedOutputs(t *testing.T) {
	requests := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/steam":
			_, _ = w.Write([]byte(`{"42":{"success":true,"data":{"name":"Game","pc_requirements":{"minimum":"RAM: 8 GB"}}}}`))
		case "/openrouter":
			requests++
			_, _ = w.Write([]byte(`{"choices":[{"message":{"content":"not structured"}}]}`))
		}
	}))
	defer server.Close()

	b := newBackend(server.Client())
	b.cacheDir = t.TempDir()
	b.steamURL = server.URL + "/steam"
	b.openRouterURL = server.URL + "/openrouter"
	b.detect = func(context.Context) (hardwareInfo, *backendError) {
		return hardwareInfo{CPU: "CPU", MemoryGB: 16}, nil
	}
	result, err := b.analyze(context.Background(), analyzeInput{AppID: 42, APIKey: "secret", Language: "en"})
	if err != nil || !result.Degraded || result.Verdict != "inconclusive" || len(result.Components) != 5 || requests != 2 {
		t.Fatalf("fallback failed: result=%+v err=%+v requests=%d", result, err, requests)
	}
}

func TestAnalyzeCachesValidatedResultForIdenticalInputs(t *testing.T) {
	requests := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/steam":
			_, _ = w.Write([]byte(`{"42":{"success":true,"data":{"name":"Game","pc_requirements":{"minimum":"RAM: 8 GB"}}}}`))
		case "/openrouter":
			requests++
			writeToolCompletion(w, validAnalysisJSON("recommended", "high"))
		}
	}))
	defer server.Close()

	b := newBackend(server.Client())
	b.cacheDir = t.TempDir()
	b.steamURL = server.URL + "/steam"
	b.openRouterURL = server.URL + "/openrouter"
	b.detect = func(context.Context) (hardwareInfo, *backendError) {
		return hardwareInfo{CPU: "CPU", MemoryGB: 16, SystemDriveFreeGB: 75.8}, nil
	}
	input := analyzeInput{AppID: 42, APIKey: "secret", Language: "pt-BR"}
	first, firstErr := b.analyze(context.Background(), input)
	second, secondErr := b.analyze(context.Background(), input)
	if firstErr != nil || secondErr != nil || requests != 1 || first.Verdict != second.Verdict {
		t.Fatalf("stable cache failed: firstErr=%+v secondErr=%+v requests=%d", firstErr, secondErr, requests)
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
