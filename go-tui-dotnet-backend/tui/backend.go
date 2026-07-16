package main

// The wire side: subscribe to the .NET backend's SSE stream and turn every `data:` line
// into a Bubble Tea message; POST operator commands back. SSE needs no client library —
// it is "read lines from an HTTP response" with reconnect-and-backoff around it.

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"
)

// RunEvent mirrors the backend's record: {seq, kind, stage, state, text}.
type RunEvent struct {
	Seq   int    `json:"seq"`
	Kind  string `json:"kind"`
	Stage string `json:"stage"`
	State string `json:"state"`
	Text  string `json:"text"`
}

type eventMsg struct{ ev RunEvent }
type connMsg struct{ connected bool }
type noteMsg struct{ text string }

// subscribe pumps SSE frames into msgs forever, reconnecting with backoff. The TUI's
// event loop never blocks on the network — it just receives messages like any other.
func subscribe(ctx context.Context, baseURL string, msgs chan<- tea.Msg) {
	backoff := []time.Duration{500, 1000, 2000, 4000, 8000}
	idx := 0
	for {
		select {
		case <-ctx.Done():
			return
		default:
		}

		err := readStream(ctx, baseURL+"/run/events", msgs)
		if err != nil {
			msgs <- connMsg{connected: false}
			if idx < len(backoff)-1 {
				idx++
			}
			select {
			case <-ctx.Done():
				return
			case <-time.After(backoff[idx] * time.Millisecond):
			}
			continue
		}
		idx = 0
	}
}

func readStream(ctx context.Context, url string, msgs chan<- tea.Msg) error {
	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("Accept", "text/event-stream")

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return fmt.Errorf("SSE status %d", resp.StatusCode)
	}

	msgs <- connMsg{connected: true}
	reader := bufio.NewReader(resp.Body)
	for {
		line, err := reader.ReadString('\n')
		if err != nil {
			if err == io.EOF {
				return nil
			}
			return err
		}
		data, ok := strings.CutPrefix(strings.TrimRight(line, "\r\n"), "data: ")
		if !ok {
			continue // event:/id:/heartbeat lines — the payload is all we render
		}
		var ev RunEvent
		if json.Unmarshal([]byte(data), &ev) == nil {
			msgs <- eventMsg{ev}
		}
	}
}

// sendCommand POSTs {"action": ...} and reports what happened as a footer note.
func sendCommand(baseURL, action string) tea.Cmd {
	return func() tea.Msg {
		body, _ := json.Marshal(map[string]string{"action": action})
		resp, err := http.Post(baseURL+"/run/command", "application/json", bytes.NewReader(body))
		if err != nil {
			return noteMsg{text: "✗ " + action + ": " + err.Error()}
		}
		defer resp.Body.Close()
		if resp.StatusCode != 200 {
			return noteMsg{text: fmt.Sprintf("✗ %s: HTTP %d", action, resp.StatusCode)}
		}
		return noteMsg{text: "→ sent " + action}
	}
}
