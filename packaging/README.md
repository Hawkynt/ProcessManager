# Packaging

## The privileged helper

`procman` runs unprivileged and reports what it may not read. A few things need root — another
user's `io` counters, their command line and environment, their open descriptors, and ending their
processes — and those go through `procman-helper`, which is the only component that ever runs
elevated (PRD §8).

Nothing here is required to run `procman`. Without it the affected columns read `—` and the affected
actions are refused with a reason, which is a supported way to run the program rather than a
degraded one.

### Linux

```sh
sudo install -Dm755 procman-helper /usr/lib/procman/procman-helper
sudo install -Dm644 org.hawkynt.procman.policy /usr/share/polkit-1/actions/org.hawkynt.procman.policy
```

The policy names `/usr/lib/procman/procman-helper` as the executable polkit will elevate. That path
is fixed by whoever installs it, not chosen by the program — a policy that elevated whatever binary
the caller pointed at would elevate anything the caller could write.

Check it without authenticating:

```sh
procman --helper-check      # starts the helper unelevated and exercises the protocol both ways
```

### Windows

Not implemented. Windows cannot both elevate a child and redirect its standard handles in one call —
the `runas` verb that raises the UAC prompt refuses redirection — so the elevated helper needs a
named pipe it connects back to. That is tracked as the remainder of milestone M7; until then the
channel reports it plainly rather than silently running the helper unelevated and reporting the same
refusals it would have anyway.
