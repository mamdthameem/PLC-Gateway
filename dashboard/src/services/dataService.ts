import type { User, Customer } from '../types';
import { isUserExpired, daysUntil } from '../utils/formatters';

const DEMO_ADMIN: User = {
  id: 'admin-1',
  email: 'admin@plc.local',
  name: 'System Admin',
  username: 'admin',
  password: 'admin123',
  role: 'admin',
  isApproved: true,
  assignedMachineIds: [],
  createdAt: new Date(),
};

export class DataService {
  private users: User[] = [];
  private customers: Customer[] = [];
  private readonly usersStorageKey = 'plc_gateway_users';

  constructor() {
    const stored = this.loadUsersFromStorage();
    if (stored && stored.length > 0) {
      this.users = stored;
      if (!this.users.some(u => u.role === 'admin')) {
        this.users.unshift(DEMO_ADMIN);
      }
    } else {
      this.users = [DEMO_ADMIN];
    }
    this.users.forEach(u => this.syncUserToCustomer(u));
    this.applyUserValidityRules();
  }

  private loadUsersFromStorage(): User[] | null {
    if (typeof window === 'undefined' || !window.localStorage) return null;
    try {
      const raw = localStorage.getItem(this.usersStorageKey);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as Array<User & { createdAt?: string; validUntil?: string }>;
      return parsed.map(u => ({
        ...u,
        createdAt: u.createdAt ? new Date(u.createdAt) : new Date(),
        validUntil: u.validUntil ? new Date(u.validUntil) : undefined,
        assignedMachineIds: u.assignedMachineIds || [],
      }));
    } catch {
      return null;
    }
  }

  private saveUsersToStorage(): void {
    if (typeof window === 'undefined' || !window.localStorage) return;
    try {
      const serializable = this.users.map(u => ({
        ...u,
        createdAt: u.createdAt ? new Date(u.createdAt).toISOString() : new Date().toISOString(),
        validUntil: u.validUntil ? new Date(u.validUntil).toISOString() : undefined,
      }));
      localStorage.setItem(this.usersStorageKey, JSON.stringify(serializable));
    } catch {
      // storage unavailable
    }
  }

  private syncUserToCustomer(user: User): void {
    if (!user.industryName) return;
    let customer = this.customers.find(c => c.name === user.industryName);
    if (!customer) {
      customer = {
        id: `customer-${Date.now()}-${Math.floor(Math.random() * 1000)}`,
        name: user.industryName,
        email: user.email,
        createdAt: new Date(),
        machineIds: [],
        userIds: [user.id],
      };
      this.customers.push(customer);
    } else if (!customer.userIds.includes(user.id)) {
      customer.userIds.push(user.id);
    }
    user.customerId = customer.id;
  }

  private applyUserValidityRules(): void {
    let updated = false;
    this.users = this.users.map(u => {
      if (isUserExpired(u.validUntil) && u.isApproved) {
        updated = true;
        return { ...u, isApproved: false };
      }
      return u;
    });
    if (updated) this.saveUsersToStorage();
  }

  getUsers(): User[] {
    this.applyUserValidityRules();
    return this.users;
  }

  getUsersExpiringWithinDays(days: number): User[] {
    this.applyUserValidityRules();
    return this.users.filter(u => {
      if (u.role === 'admin' || !u.validUntil || !u.isApproved) return false;
      if (isUserExpired(u.validUntil)) return false;
      const d = daysUntil(u.validUntil);
      return d !== null && d >= 0 && d <= days;
    });
  }

  addUser(user: User): void {
    this.users.push(user);
    this.syncUserToCustomer(user);
    this.applyUserValidityRules();
    this.saveUsersToStorage();
  }

  updateUser(updatedUser: User): void {
    const index = this.users.findIndex(u => u.id === updatedUser.id);
    if (index !== -1) {
      this.users[index] = updatedUser;
      this.syncUserToCustomer(updatedUser);
      this.applyUserValidityRules();
      this.saveUsersToStorage();
    }
  }

  deleteUser(userId: string): void {
    this.users = this.users.filter(u => u.id !== userId);
    this.saveUsersToStorage();
  }

  assignMachinesToUser(userId: string, machineIds: string[]): void {
    const user = this.users.find(u => u.id === userId);
    if (user) {
      user.assignedMachineIds = machineIds;
      this.saveUsersToStorage();
    }
  }

  getCustomers(): Customer[] {
    return this.customers;
  }

  getCustomerById(id: string): Customer | undefined {
    return this.customers.find(c => c.id === id);
  }

  addCustomer(customer: Customer): void {
    this.customers.push(customer);
  }

  // Stub — no machines in new architecture
  getMachineTemplates(): { id: string; name: string; location?: string }[] {
    return [];
  }
}

export const dataService = new DataService();
