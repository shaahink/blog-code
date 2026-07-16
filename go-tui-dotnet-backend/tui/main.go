// gated-delivery-tui — a live, interactive terminal UI in Go (Bubble Tea), attached to a
// .NET backend over Server-Sent Events.
//
// Companion to: https://shaahink.github.io/site/blog/a-live-tui-in-the-ai-era/
//
// The shape is the Elm architecture: one immutable-ish Model, one Update that folds every
// message (SSE event, keypress, resize, command result) into the next Model, one View that
// renders the whole screen from scratch. No widget tree, no invalidation bugs — the screen
// IS a function of the state.
//
// Run the backend first (dotnet run in ../backend), then:  go run .

package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"
)

func main() {
	url := flag.String("url", "http://127.0.0.1:5058", "base URL of the .NET backend")
	flag.Parse()

	msgs := make(chan tea.Msg, 256)
	go subscribe(context.Background(), *url, msgs)

	m := model{url: *url, msgs: msgs, runStatus: "connecting…"}
	if _, err := tea.NewProgram(m).Run(); err != nil {
		fmt.Fprintf(os.Stderr, "gated-delivery-tui: %v\n", err)
		os.Exit(1)
	}
}

// ── model ──────────────────────────────────────────────────────────────────────────────

type stageRow struct{ id, title, state string }
type feedLine struct{ kind, stage, text string }

type model struct {
	url  string
	msgs chan tea.Msg

	connected bool
	runStatus string
	stages    []stageRow
	feed      []feedLine
	note      string

	width, height int
}

// wait re-arms the "give me the next backend message" command after every message —
// the standard Bubble Tea pattern for pumping an external channel into the event loop.
func (m model) wait() tea.Cmd {
	return func() tea.Msg { return <-m.msgs }
}

func (m model) Init() tea.Cmd {
	return m.wait()
}

// ── update ─────────────────────────────────────────────────────────────────────────────

func (m model) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	switch msg := msg.(type) {

	case tea.WindowSizeMsg:
		m.width, m.height = msg.Width, msg.Height
		return m, nil

	case tea.KeyPressMsg:
		switch msg.String() {
		case "q", "ctrl+c":
			return m, tea.Quit
		case "p":
			return m, sendCommand(m.url, "pause")
		case "r":
			return m, sendCommand(m.url, "resume")
		case "n":
			return m, sendCommand(m.url, "restart")
		}
		return m, nil

	case connMsg:
		m.connected = msg.connected
		if !msg.connected {
			m.runStatus = "reconnecting…"
		}
		return m, m.wait()

	case noteMsg:
		m.note = msg.text
		return m, m.wait()

	case eventMsg:
		m = m.apply(msg.ev)
		return m, m.wait()
	}
	return m, nil
}

func (m model) apply(ev RunEvent) model {
	switch ev.Kind {
	case "run":
		m.runStatus = ev.State
		if ev.State == "running" && strings.Contains(ev.Text, "started") {
			m.stages = nil // a fresh run repaints the board
		}
		m.feed = append(m.feed, feedLine{"run", "", ev.Text})

	case "stage":
		found := false
		for i := range m.stages {
			if m.stages[i].id == ev.Stage {
				m.stages[i].state = ev.State
				found = true
			}
		}
		if !found {
			m.stages = append(m.stages, stageRow{ev.Stage, ev.Text, ev.State})
		}
		if ev.State == "failed" || ev.State == "delivered" {
			m.feed = append(m.feed, feedLine{"stage", ev.Stage, ev.Stage + " " + ev.State})
		}

	case "agent", "gate":
		m.feed = append(m.feed, feedLine{ev.Kind, ev.Stage, ev.Text})
	}

	if len(m.feed) > 500 {
		m.feed = m.feed[len(m.feed)-500:]
	}
	return m
}

// ── view ───────────────────────────────────────────────────────────────────────────────

var (
	accent  = lipgloss.NewStyle().Foreground(lipgloss.Color("#58A6FF")).Bold(true)
	subtle  = lipgloss.NewStyle().Foreground(lipgloss.Color("#6E7681"))
	good    = lipgloss.NewStyle().Foreground(lipgloss.Color("#3FB950"))
	bad     = lipgloss.NewStyle().Foreground(lipgloss.Color("#F85149"))
	warn    = lipgloss.NewStyle().Foreground(lipgloss.Color("#D29922"))
	text    = lipgloss.NewStyle().Foreground(lipgloss.Color("#C9D1D9"))
	panel   = lipgloss.NewStyle().Border(lipgloss.RoundedBorder()).BorderForeground(lipgloss.Color("#30363D")).Padding(0, 1)
	statusOK = lipgloss.NewStyle().Foreground(lipgloss.Color("#3FB950")).Bold(true)
)

func (m model) View() tea.View {
	if m.width < 40 || m.height < 10 {
		v := tea.NewView("terminal too small — make me bigger")
		v.AltScreen = true
		return v
	}

	header := m.renderHeader()
	footer := m.renderFooter()
	bodyH := m.height - lipgloss.Height(header) - lipgloss.Height(footer)

	stages := m.renderStages(bodyH)
	feed := m.renderFeed(m.width-lipgloss.Width(stages)-2, bodyH)
	body := lipgloss.JoinHorizontal(lipgloss.Top, stages, " ", feed)

	v := tea.NewView(lipgloss.JoinVertical(lipgloss.Left, header, body, footer))
	v.AltScreen = true
	return v
}

func (m model) renderHeader() string {
	conn := bad.Render("● offline")
	if m.connected {
		conn = statusOK.Render("● live")
	}
	status := warn.Render(m.runStatus)
	if m.runStatus == "running" {
		status = good.Render(m.runStatus)
	}
	left := accent.Render(" gated delivery ") + subtle.Render("· go tui, .net backend, sse in between")
	right := status + "  " + conn + " "
	gap := m.width - lipgloss.Width(left) - lipgloss.Width(right)
	if gap < 1 {
		gap = 1
	}
	return left + strings.Repeat(" ", gap) + right
}

func (m model) renderStages(h int) string {
	rows := make([]string, 0, len(m.stages))
	for _, s := range m.stages {
		icon, style := "·", subtle
		switch s.state {
		case "running":
			icon, style = "▶", accent
		case "failed":
			icon, style = "✗", bad
		case "delivered":
			icon, style = "✓", good
		}
		rows = append(rows, style.Render(fmt.Sprintf("%s %s %s", icon, s.id, s.title)))
	}
	if len(rows) == 0 {
		rows = append(rows, subtle.Render("waiting for the run…"))
	}
	return panel.Height(h - 2).Render(strings.Join(rows, "\n"))
}

func (m model) renderFeed(w, h int) string {
	visible := h - 2 // panel border
	if visible < 1 {
		visible = 1
	}
	start := len(m.feed) - visible
	if start < 0 {
		start = 0
	}

	lines := make([]string, 0, visible)
	for _, l := range m.feed[start:] {
		style, prefix := text, "  "
		switch {
		case l.kind == "run":
			style, prefix = accent, "◆ "
		case l.kind == "stage":
			style, prefix = warn, "▸ "
		case l.kind == "gate" && strings.Contains(l.text, "FAIL"):
			style, prefix = bad, "⛔ "
		case l.kind == "gate":
			style, prefix = good, "✓ "
		}
		s := prefix + l.text
		if r := []rune(s); w > 8 && len(r) > w-4 {
			s = string(r[:w-5]) + "…"
		}
		lines = append(lines, style.Render(s))
	}
	return panel.Width(w).Height(h - 2).Render(strings.Join(lines, "\n"))
}

func (m model) renderFooter() string {
	keys := subtle.Render(" p pause · r resume · n new run · q quit")
	if m.note == "" {
		return keys
	}
	return keys + subtle.Render("   │ ") + warn.Render(m.note)
}
