Use the host's native shell — you have both a `Bash` tool and a `PowerShell` tool, each with its own
syntax and path convention — and only route commands through a container/VM/subsystem when the repo
says to. Never carry over a shell or environment assumption from another project.
