# Agent Evaluations (`02-Radar-Agent-Api`)

Questo documento definisce il set di test qualitativi (Evals) per verificare il comportamento dell'agente `DatabaseSupportAgent`, la corretta selezione dei tool (`SearchTickets`, `CreateTickets`) e il mantenimento del contesto tramite `conversationId`.

---

## Eval 1: Creazione di un nuovo ticket con priorità esplicita
* **Conversation ID:** `eval-01`
* **Domanda Utente:** `"Apri un ticket urgente con priorità Critical perché il server di posta aziendale non invia email."`
* **Tool Atteso:** `CreateTickets(title: "Server di posta aziendale non invia email", priority: "Critical")`
* **Risposta Attesa:** L'agente conferma l'avvenuta registrazione del ticket nel database, riportando l'ID assegnato, il titolo, la priorità `Critical` e lo stato iniziale `Open`.

---

## Eval 2: Ricerca filtrata dei soli ticket aperti
* **Conversation ID:** `eval-02`
* **Domanda Utente:** `"Quali sono i ticket attualmente aperti nel sistema?"`
* **Tool Atteso:** `SearchTickets(status: "Open")`
* **Risposta Attesa:** L'agente interroga il database filtrando esclusivamente per stato `Open` ed elenca i ticket aperti (ignorando eventuali ticket con stato `Closed`).

---

## Eval 3: Ricerca generale senza filtri di stato
* **Conversation ID:** `eval-03`
* **Domanda Utente:** `"Mostrami lo storico completo di tutti i ticket presenti nel database, sia aperti che chiusi."`
* **Tool Atteso:** `SearchTickets(status: null)` (oppure vuoto)
* **Risposta Attesa:** L'agente invoca `SearchTickets` senza restringere lo stato e restituisce l'elenco completo di tutti i record salvati su SQL Server.

---

## Eval 4: Gestione di ricerca senza risultati (Edge Case)
* **Conversation ID:** `eval-04`
* **Domanda Utente:** `"Ci sono ticket già chiusi nel database?"` *(presupponendo che non esistano ticket con stato `Closed`)*
* **Tool Atteso:** `SearchTickets(status: "Closed")`
* **Risposta Attesa:** L'agente non inventa ticket inesistenti (nessuna allucinazione), ma informa chiaramente l'utente che al momento non risultano ticket con stato `Closed`.

---

## Eval 5: Conversazione multi-turno e comprensione del contesto
* **Conversation ID:** `eval-05`
* **Turno 1 — Domanda Utente:** `"Registra una nuova segnalazione con priorità High: la stampante al piano terra è inceppata."`
  * **Tool Atteso:** `CreateTickets(title: "Stampante al piano terra inceppata", priority: "High")`
* **Turno 2 — Domanda Utente (ellittica):** `"Perfetto, e quali sono aperti adesso?"`
  * **Tool Atteso:** `SearchTickets(status: "Open")`
  * **Risposta Attesa:** Grazie alla `AgentSession` associata a `eval-05`, l'agente comprende che la domanda si riferisce ai ticket di supporto, interroga il database e include nella lista anche il ticket della stampante appena creato nel turno precedente.