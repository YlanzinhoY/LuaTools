package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"net/http"
	"os"
	"strings"
	"time"
)

func main() {
	appID := flag.Int64("appid", 0, "Steam App ID")
	gameName := flag.String("game-name", "", "game name shown by LuaTools")
	language := flag.String("language", "en", "BCP-47 response language")
	languageName := flag.String("language-name", "", "English name of the response language")
	flag.Parse()

	if *appID <= 0 {
		writeOutput(commandOutput{Success: false, Error: &outputError{Code: "invalid_appid", Message: "A valid Steam App ID is required."}})
		os.Exit(2)
	}

	apiKey := strings.TrimSpace(os.Getenv("OPENROUTER_API_KEY"))
	if apiKey == "" {
		writeOutput(commandOutput{Success: false, Error: &outputError{Code: "missing_api_key", Message: "An OpenRouter API key is required."}})
		os.Exit(2)
	}

	ctx, cancel := context.WithTimeout(context.Background(), 110*time.Second)
	defer cancel()

	backend := newBackend(&http.Client{Timeout: 90 * time.Second})
	result, err := backend.analyze(ctx, analyzeInput{
		AppID:        *appID,
		GameName:     strings.TrimSpace(*gameName),
		Language:     normalizedLanguage(*language),
		LanguageName: strings.TrimSpace(*languageName),
		APIKey:       apiKey,
	})
	if err != nil {
		writeOutput(commandOutput{Success: false, Error: &outputError{Code: err.Code, Message: err.Message}})
		os.Exit(1)
	}

	writeOutput(commandOutput{Success: true, Result: result})
}

func writeOutput(value commandOutput) {
	encoder := json.NewEncoder(os.Stdout)
	encoder.SetEscapeHTML(false)
	if err := encoder.Encode(value); err != nil {
		fmt.Fprintln(os.Stderr, "could not encode result")
	}
}

func normalizedLanguage(value string) string {
	value = strings.TrimSpace(value)
	if value == "" {
		return "en"
	}
	if len(value) > 32 {
		return value[:32]
	}
	return value
}
